using Titan.Domain.Aggregates.Vm;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Queries.Compute.VolumeSnapshot.Contracts;
using VolSnapCmd = Titan.Application.Commands.Compute.VolumeSnapshot;
using VolSnapQry = Titan.Application.Queries.Compute.VolumeSnapshot;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// A snapshot is written through its volume (titan-ops architecture/code-structure.md#child-entity): the create, delete,
// restore and callback handlers load the volume locked (a callback, the one its route names), change it and save it
// through IVolumeRepository. The snapshot repository is read side only, so only the list query asks it.
public class VolumeSnapshotServiceTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;
    // The tenant's Incus project, as titan-ops architecture/tenancy-and-admin.md#project-name spells it.
    private static string Project => "t-" + TenantId.ToString("N");

    private readonly Mock<IVolumeSnapshotRepository> _snapshotRepo = new();
    private readonly Mock<IVolumeRepository> _volumeRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IActivityRecorder> _activity = new();

    // titan-ops architecture/tenancy-and-admin.md#tenant-quota — the handlers load the tenant to enforce its
    // quota; Tenant.Create's default caps are what the quota assertions in this file count against.
    private readonly Tenant _tenant = TestTenant.Active("t", "kc", "t@x.io");

    public VolumeSnapshotServiceTests()
        => _tenantRepo
            .Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_tenant);

    // Facade over the five volume-snapshot handlers so the tests keep one call shape.
    private VolumeSnapshotFacade CreateService() => new(
        _snapshotRepo.Object, _volumeRepo.Object, _tenantRepo.Object, _currentUser.Object, _commands.Object, _activity.Object);

    private sealed class VolumeSnapshotFacade(
        IVolumeSnapshotRepository snapshotRepo,
        IVolumeRepository volumeRepo,
        ITenantRepository tenantRepo,
        ICurrentUser currentUser,
        IProviderCommandQueue commands,
        IActivityRecorder activity)
    {
        public Task<CreateVolumeSnapshotCommandResult> CreateAsync(Guid volumeId, CreateVolumeSnapshotBody body, CancellationToken ct = default)
            => new VolSnapCmd.CreateVolumeSnapshotCommandHandler(volumeRepo, tenantRepo, currentUser, commands, activity)
                .HandleAsync(new CreateVolumeSnapshotCommand(volumeId, body.Name), ct);

        public Task<List<VolumeSnapshotQueryResult>> ListAsync(Guid volumeId, CancellationToken ct = default)
            => new VolSnapQry.ListVolumeSnapshotsQueryHandler(snapshotRepo, volumeRepo, currentUser)
                .HandleAsync(new ListVolumeSnapshotsQuery(volumeId), ct);

        public Task DeleteAsync(Guid volumeId, Guid snapshotId, CancellationToken ct = default)
            => new VolSnapCmd.DeleteVolumeSnapshotCommandHandler(volumeRepo, currentUser, commands, activity)
                .HandleAsync(new DeleteVolumeSnapshotCommand(volumeId, snapshotId), ct);

        public Task<RestoreVolumeSnapshotCommandResult> RestoreAsync(Guid volumeId, Guid snapshotId, RestoreVolumeSnapshotBody body, CancellationToken ct = default)
            => new VolSnapCmd.RestoreVolumeSnapshotCommandHandler(volumeRepo, tenantRepo, currentUser, commands, activity)
                .HandleAsync(new RestoreVolumeSnapshotCommand(volumeId, snapshotId, body.Name), ct);

        public Task EndRequestAsync(EndVolumeSnapshotRequestCommand report, CancellationToken ct = default)
            => new VolSnapCmd.EndVolumeSnapshotRequestCommandHandler(volumeRepo, activity).HandleAsync(report, ct);

        public Task ObserveAsync(ObserveVolumeSnapshotCommand observation, CancellationToken ct = default)
            => new VolSnapCmd.ObserveVolumeSnapshotCommandHandler(volumeRepo).HandleAsync(observation, ct);
    }

    // Test-local request bodies; the facade unpacks them into commands.
    private record CreateVolumeSnapshotBody(string Name);
    private record RestoreVolumeSnapshotBody(string Name);

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_VolumeAvailable_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);

        var result = await CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-01"), default);

        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so the state is
        // asserted on the snapshot the volume now holds — the aggregate that owns it.
        Assert.NotEqual(Guid.Empty, result.SnapshotId);
        VolumeSnapshot added = Assert.Single(volume.Snapshots);
        Assert.Equal(result.SnapshotId, added.Id);
        Assert.Equal(VolumeSnapshotStatus.Creating, added.Status);
        _volumeRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVolumeSnapshotCreateAsync(
            It.Is<global::Titan.Domain.Aggregates.Volume.Volume>(a81 => a81.Id == volume.Id && a81.ClusterId == volume.ClusterId), It.Is<global::Titan.Domain.Aggregates.Volume.VolumeSnapshot>(a82 => a82.Id == added.Id), added.Operation.PendingId!.Value, "cs-vol-1", Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The volume is loaded locked (titan-ops architecture/code-structure.md#child-entity): the cap is counted on what
    // it holds, so a second create at the cap must wait for this one to commit.
    [Fact]
    public async Task Create_LoadsTheVolumeForUpdate_NotTheUnlockedRead()
    {
        SetupCurrentUser(TenantId);
        SetupVolume(MakeVolume(VolSeed.Available, csId: "cs-vol-1"));

        await CreateService().CreateAsync(_volumeId, new CreateVolumeSnapshotBody("snap-01"), default);

        _volumeRepo.Verify(r => r.FindByIdForUpdateAsync(_volumeId, It.IsAny<CancellationToken>()), Times.Once);
        _volumeRepo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_NoTenantContext_ThrowsForbiddenException()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateService().CreateAsync(Guid.NewGuid(), new CreateVolumeSnapshotBody("snap"), default));

        Assert.Equal("NOT_A_MEMBER", ex.Code);
    }

    [Fact]
    public async Task Create_VolumeAttached_Success_Allowed()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Attached, csId: "cs-vol-2");
        SetupVolume(volume);

        var result = await CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-attached"), default);

        VolumeSnapshot added = Assert.Single(volume.Snapshots);
        Assert.Equal(result.SnapshotId, added.Id);
        Assert.Equal(VolumeSnapshotStatus.Creating, added.Status);
        _volumeRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // The cap is the tenant's default (3) counted on the volume's own snapshots: three Incus holds fill it.
    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        for (int i = 0; i < 3; i++)
        {
            volume.WithSnapshot($"snap-{i}").MarkCreated($"cs-vol-1/s{i}");
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-over"), default));

        Assert.Equal("VOLUME_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
        Assert.Equal(3, volume.Snapshots.Count);
        _volumeRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVolumeSnapshotCreateAsync(
            It.IsAny<global::Titan.Domain.Aggregates.Volume.Volume>(), It.IsAny<global::Titan.Domain.Aggregates.Volume.VolumeSnapshot>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The handler asks about quota before the volume checks itself (titan-ops architecture/code-structure.md#quota-gate).
    [Fact]
    public async Task Create_AtTheCap_OnAVolumeNotResting_AnswersTheQuota()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        for (int i = 0; i < 3; i++)
        {
            volume.WithSnapshot($"snap-{i}").MarkCreated($"cs-vol-1/s{i}");
        }
        volume.MarkNotFound();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-over"), default));

        Assert.Equal("VOLUME_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
    }

    // A snapshot whose delete is waiting stopped counting the moment it was asked for (DCS-Q1), so the cap is not full.
    [Fact]
    public async Task Create_AtTheCapWithOneSnapshotBeingDeleted_IsAccepted()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        for (int i = 0; i < 3; i++)
        {
            volume.WithSnapshot($"snap-{i}").MarkCreated($"cs-vol-1/s{i}");
        }
        volume.Snapshots.First().Delete();

        var result = await CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-3"), default);

        Assert.Contains(volume.Snapshots, s => s.Id == result.SnapshotId);
        Assert.Equal(3, volume.SnapshotQuotaUsage);
    }

    [Fact]
    public async Task Create_VolumeBeingCreated_ThrowsVolumeInvalidState()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Creating);
        SetupVolume(volume);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap"), default));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
        Assert.Empty(volume.Snapshots);
    }

    [Fact]
    public async Task Create_VolumeAttaching_ThrowsVolumeInvalidState()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Attaching, csId: "cs-vol-1");
        SetupVolume(volume);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap"), default));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }

    [Fact]
    public async Task Create_VolumeNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        _volumeRepo.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Volume?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(Guid.NewGuid(), new CreateVolumeSnapshotBody("snap"), default));

        Assert.Equal("VOLUME_NOT_FOUND", ex.Code);
    }

    // A foreign volume answers like a missing one (titan-ops architecture/http-contract.md#tenant-scoping).
    [Fact]
    public async Task Create_VolumeOfAnotherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1", owner: TestTenant.WithRoomToSpare());
        SetupVolume(volume);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap"), default));

        Assert.Equal("VOLUME_NOT_FOUND", ex.Code);
        Assert.Empty(volume.Snapshots);
    }

    // ── DeleteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_SnapshotOnIncus_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Exists, csSnapId: "cs-snap-1");

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(VolumeSnapshotStatus.Deleting, snapshot.Status);
        _volumeRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            It.Is<global::Titan.Domain.Aggregates.Volume.Volume>(a85 => a85.Id == volume.Id && a85.ClusterId == volume.ClusterId), It.Is<global::Titan.Domain.Aggregates.Volume.VolumeSnapshot>(a86 => a86.Id == snapshot.Id), snapshot.Operation.PendingId!.Value, "cs-snap-1", Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_SnapshotCreating_ThrowsSnapshotInvalidState()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Creating);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().DeleteAsync(volume.Id, snapshot.Id, default));

        Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    // Incus made the snapshot before the create command answered: once its id arrived, the delete reaches Incus.
    [Fact]
    public async Task Delete_AfterTheCreateReportedItsId_GoesToIncus()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Creating);
        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Operation.PendingId!.Value, "Done", "cs-snap-9"));

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(VolumeSnapshotStatus.Deleting, snapshot.Status);
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            It.Is<global::Titan.Domain.Aggregates.Volume.Volume>(a87 => a87.Id == volume.Id && a87.ClusterId == volume.ClusterId), It.Is<global::Titan.Domain.Aggregates.Volume.VolumeSnapshot>(a88 => a88.Id == snapshot.Id), snapshot.Operation.PendingId!.Value, "cs-snap-9", Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory] // titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat: the aggregate refuses a repeat; the handler recognises it and answers quietly
    [InlineData(SnapSeed.Deleting)]
    [InlineData(SnapSeed.Deleted)]
    public async Task Delete_RepeatedOrDone_IsANoOp_EnqueuesNoCommand(SnapSeed from)
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, from, csSnapId: "cs-snap-1");
        var before = (snapshot.Status, snapshot.Operation.PendingId);

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(before, (snapshot.Status, snapshot.Operation.PendingId));
        _volumeRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            It.IsAny<global::Titan.Domain.Aggregates.Volume.Volume>(), It.IsAny<global::Titan.Domain.Aggregates.Volume.VolumeSnapshot>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_SnapshotNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(volume.Id, Guid.NewGuid(), default));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    // The snapshot exists, on another volume of the same tenant: the volume asked does not have it, so 404.
    [Fact]
    public async Task Delete_SnapshotOfAnotherVolume_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var otherVolume = MakeVolume(VolSeed.Available, csId: "cs-vol-2");
        var snapshot = MakeSnapshot(otherVolume, SnapSeed.Exists, csSnapId: "cs-snap-1");

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(volume.Id, snapshot.Id, default));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", ex.Code);
        Assert.Equal("Available/Available", Shape(snapshot));
    }

    // ── RestoreAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_SnapshotOnIncus_CreatesNewVolumeAndEnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Exists, csSnapId: "cs-snap-1");
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(5);
        Volume? added = null;
        _volumeRepo.Setup(r => r.Add(It.IsAny<Volume>())).Callback<Volume>(v => added = v);

        var result = await CreateService().RestoreAsync(volume.Id, snapshot.Id,
            new RestoreVolumeSnapshotBody("vol-restored-01"), default);

        Assert.NotEqual(Guid.Empty, result.NewVolumeId);
        _volumeRepo.Verify(r => r.Add(It.Is<Volume>(v => v.Name == "vol-restored-01" && v.ClusterId == volume.ClusterId)), Times.Once);
        _volumeRepo.Verify(r => r.SaveAsync(default), Times.Once);
        // The restore is the new volume's create, so the command carries that volume's request id.
        _commands.Verify(w => w.EnqueueVolumeSnapshotRestoreAsync(
            It.Is<global::Titan.Domain.Aggregates.Volume.Volume>(a91 => a91.Id == result.NewVolumeId && a91.Name == "vol-restored-01" && a91.ClusterId == volume.ClusterId), added!.Operation.PendingId!.Value, "cs-snap-1", "t-" + _tenant.Id.ToString("N"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // Only the snapshot must rest on Incus: a source volume the reconciler no longer finds still restores
    // (titan-ops features/volume/design.md#volume-restore-is-a-new-volume). A Deleted volume has no live snapshot to
    // restore from, since its delete took its snapshots with it.
    [Fact]
    public async Task Restore_SourceVolumeNotFoundOnIncus_StillSucceeds()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Exists, csSnapId: "cs-snap-1");
        volume.MarkNotFound();
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        var result = await CreateService().RestoreAsync(volume.Id, snapshot.Id,
            new RestoreVolumeSnapshotBody("vol-from-old-snap"), default);

        Assert.NotEqual(Guid.Empty, result.NewVolumeId);
    }

    [Fact]
    public async Task Restore_SnapshotBeingCreated_ThrowsSnapshotInvalidState()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Creating);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RestoreAsync(volume.Id, snapshot.Id,
                new RestoreVolumeSnapshotBody("vol-restore"), default));

        Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    [Fact]
    public async Task Restore_VolumeQuotaExceeded_ThrowsVolumeQuotaExceeded()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Exists, csSnapId: "cs-snap-1");
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(10);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RestoreAsync(volume.Id, snapshot.Id,
                new RestoreVolumeSnapshotBody("vol-restore"), default));

        Assert.Equal("VOLUME_QUOTA_EXCEEDED", ex.Code);
    }

    // A snapshot that cannot be restored answers that first, whatever the quota.
    [Fact]
    public async Task Restore_OfASnapshotNotRestorable_AtTheVolumeCap_AnswersTheSnapshot()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Creating);
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(10);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RestoreAsync(volume.Id, snapshot.Id,
                new RestoreVolumeSnapshotBody("vol-restore"), default));

        Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    [Fact]
    public async Task Restore_SnapshotOfAnotherVolume_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(MakeVolume(VolSeed.Available, csId: "cs-vol-2"), SnapSeed.Exists, csSnapId: "cs-snap-1");

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().RestoreAsync(volume.Id, snapshot.Id, new RestoreVolumeSnapshotBody("vol-restore"), default));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", ex.Code);
        _volumeRepo.Verify(r => r.Add(It.IsAny<Volume>()), Times.Never);
    }

    // ── Callbacks (titan-ops features/volume/design.md#volume-reports) ──────────────

    [Fact]
    public async Task EndRequest_DoneOfTheCreate_StoresTheId_AndEndsIt()
    {
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Creating);

        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Operation.PendingId!.Value, "Done", "vol1/s1"));

        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal("vol1/s1", snapshot.ExternalId);
        _volumeRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact]
    public async Task EndRequest_FailedCreate_RecordsWhy()
    {
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Creating);

        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Operation.PendingId!.Value,
            "Failed", Reason: "Snapshot creation failed.", Detail: "raw"));

        Assert.Equal("Failed/-", Shape(snapshot));
        Assert.Equal("Snapshot creation failed.", snapshot.Operation.FailureReason);
        Assert.Equal("raw", snapshot.Operation.FailureDetail);
    }

    // The give-up found the snapshot on Incus: the id is kept and the snapshot is Available, so the delete reaches Incus,
    // but the create still failed — it records why, and a failed create has no outcome.
    [Fact]
    public async Task EndRequest_FailedCreateCarryingTheId_RecordsTheIdAndWhy_WithoutSnapshotCreated()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Creating);

        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Operation.PendingId!.Value,
            "Failed", "vol1/s1", Reason: "Snapshot creation failed."));

        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal("vol1/s1", snapshot.ExternalId);
        Assert.Equal("Snapshot creation failed.", snapshot.Operation.FailureReason);
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            It.Is<global::Titan.Domain.Aggregates.Volume.Volume>(a92 => a92.Id == volume.Id && a92.ClusterId == volume.ClusterId), It.Is<global::Titan.Domain.Aggregates.Volume.VolumeSnapshot>(a93 => a93.Id == snapshot.Id), snapshot.Operation.PendingId!.Value, "vol1/s1", Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EndRequest_FailedDelete_GivesTheSnapshotBack_WithTheReason_AndItCanBeDeletedAgain()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Deleting, csSnapId: "cs-snap-1");

        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Operation.PendingId!.Value,
            "Failed", Reason: "Snapshot deletion failed.", Detail: "refused"));

        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal("Snapshot deletion failed.", snapshot.Operation.FailureReason);
        _activity.Verify(a => a.StageOutcome(TenantId, "volume_snapshot.delete_failed", "volume_snapshot", snapshot.Id, "snap-01"), Times.Once);

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(VolumeSnapshotStatus.Deleting, snapshot.Status);
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            It.Is<global::Titan.Domain.Aggregates.Volume.Volume>(a94 => a94.Id == volume.Id && a94.ClusterId == volume.ClusterId), It.Is<global::Titan.Domain.Aggregates.Volume.VolumeSnapshot>(a95 => a95.Id == snapshot.Id), snapshot.Operation.PendingId!.Value, "cs-snap-1", Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EndRequest_DoneOfTheDelete_EndsIt()
    {
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Deleting);

        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, snapshot.Operation.PendingId!.Value, "Done"));

        Assert.Equal("Deleted/-", Shape(snapshot));
    }

    // Only the delete command ends a delete: the reconciler not finding the snapshot is only remembered as seen gone.
    [Fact]
    public async Task Observe_NotFound_WhileADeleteWaits_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Deleting);

        await CreateService().ObserveAsync(new ObserveVolumeSnapshotCommand(volume.Id, snapshot.Id, false));

        Assert.Equal("Deleting/-", Shape(snapshot));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Observe_NotFound_OnASnapshotStillWanted_IsNotFound_AndSeenAgainExists()
    {
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume, SnapSeed.Exists);

        await CreateService().ObserveAsync(new ObserveVolumeSnapshotCommand(volume.Id, snapshot.Id, false));
        Assert.Equal("NotFound/-", Shape(snapshot));

        await CreateService().ObserveAsync(new ObserveVolumeSnapshotCommand(volume.Id, snapshot.Id, true));
        Assert.Equal("Available/Available", Shape(snapshot));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    // The snapshot is reached through the volume the route names (titan-ops architecture/code-structure.md#child-entity):
    // no such volume, no such snapshot.
    [Fact]
    public async Task EndRequest_SnapshotNotFound_ThrowsNotFoundException()
    {
        _volumeRepo.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Volume?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Done")));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    // The route names another volume, one that exists but does not have the snapshot: not found, and nothing changes.
    [Fact]
    public async Task EndRequest_SnapshotUnderAnotherVolume_ThrowsNotFoundException()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume, SnapSeed.Deleting);
        var otherVolume = MakeVolume(VolSeed.Available, csId: "cs-vol-2");
        SetupVolume(volume);
        SetupVolume(otherVolume);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(otherVolume.Id, snapshot.Id, snapshot.Operation.PendingId!.Value, "Done")));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", ex.Code);
        Assert.Equal("Deleting/Available", Shape(snapshot));
        _volumeRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Activity: one request, one outcome (titan-ops features/activity/design.md#request-and-outcome-names) ─────

    [Fact]
    public async Task Create_RecordsTheRequest_AndTheCallbackRecordsTheOutcomeOnce()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);

        var result = await CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("nightly"), default);

        _activity.Verify(a => a.Stage("volume_snapshot.create_requested", "volume_snapshot", result.SnapshotId, "nightly"), Times.Once);

        VolumeSnapshot saved = Assert.Single(volume.Snapshots);
        Guid createId = saved.Operation.PendingId!.Value;
        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, saved.Id, createId, "Done", "cs-snap-1"));
        await CreateService().ObserveAsync(new ObserveVolumeSnapshotCommand(volume.Id, saved.Id, true));
        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, saved.Id, createId, "Done", "cs-snap-1"));

        _activity.Verify(a => a.StageOutcome(saved.TenantId, "volume_snapshot.created", "volume_snapshot", saved.Id, "nightly"), Times.Once);
    }

    [Fact]
    public async Task Delete_RecordsTheRequest_AndTheCallbackRecordsTheOutcomeOnce()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snap = MakeSnapshot(volume, SnapSeed.Exists);

        await CreateService().DeleteAsync(volume.Id, snap.Id, default);
        await CreateService().DeleteAsync(volume.Id, snap.Id, default); // repeat while deleting: nothing more

        _activity.Verify(a => a.Stage("volume_snapshot.delete_requested", "volume_snapshot", snap.Id, "snap-01"), Times.Once);

        Guid deleteId = snap.Operation.PendingId!.Value;
        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snap.Id, deleteId, "Done"));
        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snap.Id, deleteId, "Done"));

        _activity.Verify(a => a.StageOutcome(TenantId, "volume_snapshot.deleted", "volume_snapshot", snap.Id, "snap-01"), Times.Once);
    }

    // A restore creates a NEW volume: the request is recorded against that volume, and its outcome is the
    // "volume.created" the volume callback records when the restore command reports the new volume's id.
    [Fact]
    public async Task Restore_RecordsTheRequestAgainstTheNewVolume()
    {
        SetupCurrentUser(TenantId);
        var source = MakeVolume(VolSeed.Available);
        SetupVolume(source);
        var snap = MakeSnapshot(source, SnapSeed.Exists);
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        var result = await CreateService().RestoreAsync(source.Id, snap.Id, new RestoreVolumeSnapshotBody("restored-01"), default);

        _activity.Verify(a => a.Stage("volume_snapshot.restore_requested", "volume", result.NewVolumeId, "restored-01"), Times.Once);
    }

    [Fact]
    public async Task FailedCallback_RecordsNoOutcome()
    {
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snap = MakeSnapshot(volume, SnapSeed.Creating);

        await CreateService().EndRequestAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snap.Id, snap.Operation.PendingId!.Value, "Failed", Reason: "boom"));

        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    // A create that failed before Incus had the snapshot leaves the list at once, with no command
    // (titan-ops architecture/desired-and-current.md#delete-locally).
    [Fact]
    public async Task Delete_OfACreateThatFailedBeforeIncusHadIt_DeletesLocally_AndSendsNothing()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snap = MakeSnapshot(volume, SnapSeed.CreateFailed);

        await CreateService().DeleteAsync(volume.Id, snap.Id, default);

        Assert.Equal("Deleted/-", Shape(snap));
        Assert.Empty(_commands.Invocations);
        _activity.Verify(a => a.StageOutcome(TenantId, "volume_snapshot.deleted", "volume_snapshot", snap.Id, "snap-01"), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Guid _volumeId;

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    /// The volume every handler here loads, locked: the create, delete and restore, and the callback by the volume the
    /// route names. Only the locked read is set up, so a handler that took the unlocked one would find no volume. One
    /// instance, so a test reads the handler's writes off it.
    private void SetupVolume(Volume volume)
    {
        _volumeId = volume.Id;
        _volumeRepo.Setup(r => r.FindByIdForUpdateAsync(volume.Id, default)).ReturnsAsync(volume);
    }

    // The status, then whether Incus was last known to hold the snapshot (InstanceStatus, "-" when not): while an
    // operation waits the status alone does not say it.
    private static string Shape(VolumeSnapshot s)
    {
        string seen = "-";
        if (s.InstanceStatus != null)
        {
            seen = s.InstanceStatus.Value.ToString();
        }
        return $"{s.Status}/{seen}";
    }

    // Situations named in the tests' vocabulary: Attaching/Detaching/Deleting mean "that request is waiting".
    public enum VolSeed { Creating, Available, Attaching, Attached, Detaching, Deleting, Deleted, NotFound }
    public enum SnapSeed { Creating, CreateFailed, Exists, Deleting, Deleted }

    private static Volume MakeVolume(VolSeed status, string? csId = null, Titan.Domain.Aggregates.Tenant.Tenant? owner = null)
    {
        var vol = Volume.Create((owner ?? TenantIdOwner).Id, "vol-01", "cluster-1", "offering-1", 50);
        var effectiveId = csId ?? "cs-vol-default";
        var vm = Vm.Create(TenantIdOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-1", 20);
        vm.MarkRunning("cs-vm-1", "10.0.0.5");
        switch (status)
        {
            case VolSeed.Available: vol.MarkCreated(effectiveId); break;
            case VolSeed.Attaching: vol.MarkCreated(effectiveId); vol.Attach(vm); break;
            case VolSeed.Attached: vol.MarkCreated(effectiveId); EndAttachDone(vol, vm); break;
            case VolSeed.Detaching: vol.MarkCreated(effectiveId); EndAttachDone(vol, vm); vol.Detach(); break;
            case VolSeed.Deleting: vol.MarkCreated(effectiveId); vol.Delete(); break;
            case VolSeed.Deleted: vol.MarkCreated(effectiveId); vol.Delete(); vol.MarkDeleted(); break;
            case VolSeed.NotFound: vol.MarkCreated(effectiveId); vol.MarkNotFound(); break;
        }
        return vol;
    }

    // Only the attach command's own report ends the attach; an observation of the VM using the volume would not.
    private static void EndAttachDone(Volume vol, Vm vm)
    {
        VolumeAttachProviderIds attach = vol.Attach(vm);
        vol.AttachSucceeded(attach.OperationId, null, true, vm.Id, false);
    }

    // A snapshot of the volume, in the shape named (titan-ops architecture/code-structure.md#child-entity).
    private static VolumeSnapshot MakeSnapshot(Volume volume, SnapSeed status, string? csSnapId = null)
    {
        var snap = volume.WithSnapshot("snap-01");
        switch (status)
        {
            case SnapSeed.CreateFailed: snap.MarkRequestFailed("could not create", "raw"); break;
            case SnapSeed.Exists: snap.MarkCreated(csSnapId ?? "cs-snap-default"); break;
            case SnapSeed.Deleting: snap.MarkCreated(csSnapId ?? "cs-snap-default"); snap.Delete(); break;
            case SnapSeed.Deleted: snap.MarkCreated("x"); snap.Delete(); snap.MarkDeleted(); break;
        }
        return snap;
    }

    // titan-ops architecture/desired-and-current.md#request-failure: the raw error is admin-only.
    [Fact]
    public void TenantVolumeSnapshotQueryResult_HasNoFailureDetailField_NorAStateWord()
    {
        Assert.Null(typeof(VolumeSnapshotQueryResult).GetProperty("RequestFailureDetail"));
        Assert.Null(typeof(VolumeSnapshotQueryResult).GetProperty("CurrentState"));
        Assert.Null(typeof(VolumeSnapshotQueryResult).GetProperty("DesiredState"));
        Assert.NotNull(typeof(VolumeSnapshotQueryResult).GetProperty("RequestFailureReason"));
        Assert.Null(typeof(VolumeSnapshotQueryResult).GetProperty("CurrentExistence"));
        Assert.Null(typeof(VolumeSnapshotQueryResult).GetProperty("DesiredExistence"));
        Assert.Null(typeof(VolumeSnapshotQueryResult).GetProperty("Transition"));
        Assert.NotNull(typeof(VolumeSnapshotQueryResult).GetProperty("Status"));
    }
}

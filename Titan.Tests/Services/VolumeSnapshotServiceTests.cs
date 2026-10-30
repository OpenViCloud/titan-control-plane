using Titan.Domain.Aggregates.Vm;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Queries.Compute.VolumeSnapshot.Contracts;
using VolSnapCmd = Titan.Application.Commands.Compute.VolumeSnapshot;
using VolSnapQry = Titan.Application.Queries.Compute.VolumeSnapshot;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.VolumeSnapshot;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class VolumeSnapshotServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
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
            => new VolSnapCmd.CreateVolumeSnapshotCommandHandler(snapshotRepo, volumeRepo, tenantRepo, currentUser, commands, activity)
                .HandleAsync(new CreateVolumeSnapshotCommand(volumeId, body.Name), ct);

        public Task<List<VolumeSnapshotQueryResult>> ListAsync(Guid volumeId, CancellationToken ct = default)
            => new VolSnapQry.ListVolumeSnapshotsQueryHandler(snapshotRepo, volumeRepo, currentUser)
                .HandleAsync(new ListVolumeSnapshotsQuery(volumeId), ct);

        public Task DeleteAsync(Guid volumeId, Guid snapshotId, CancellationToken ct = default)
            => new VolSnapCmd.DeleteVolumeSnapshotCommandHandler(snapshotRepo, volumeRepo, currentUser, commands, activity)
                .HandleAsync(new DeleteVolumeSnapshotCommand(volumeId, snapshotId), ct);

        public Task<RestoreVolumeSnapshotCommandResult> RestoreAsync(Guid volumeId, Guid snapshotId, RestoreVolumeSnapshotBody body, CancellationToken ct = default)
            => new VolSnapCmd.RestoreVolumeSnapshotCommandHandler(snapshotRepo, volumeRepo, tenantRepo, currentUser, commands, activity)
                .HandleAsync(new RestoreVolumeSnapshotCommand(volumeId, snapshotId, body.Name), ct);

        public Task UpdateStatusAsync(Guid volumeId, Guid snapshotId, UpdateVolumeSnapshotStatusBody body, CancellationToken ct = default)
            => new VolSnapCmd.UpdateVolumeSnapshotStatusCommandHandler(snapshotRepo, activity)
                .HandleAsync(new UpdateVolumeSnapshotStatusCommand(volumeId, snapshotId, body.Report, body.Exists, body.ExternalId,
                    body.Reason, body.Detail), ct);
    }

    // Test-local request bodies; the facade unpacks them into commands.
    private record CreateVolumeSnapshotBody(string Name);
    private record RestoreVolumeSnapshotBody(string Name);
    private record UpdateVolumeSnapshotStatusBody(
        string Report, bool? Exists = null, string? ExternalId = null, string? Reason = null, string? Detail = null);

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_VolumeAvailable_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        _snapshotRepo.Setup(r => r.CountActiveByVolumeIdAsync(volume.Id, default)).ReturnsAsync(0);

        var result = await CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-01"), default);

        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so status
        // is asserted on the persisted snapshot.
        Assert.NotEqual(Guid.Empty, result.SnapshotId);
        _snapshotRepo.Verify(r => r.SaveAsync(
            It.Is<VolumeSnapshot>(x => x.Id == result.SnapshotId
                                    && x.Transition == VolumeSnapshotTransition.Creating), default), Times.Once);
        _commands.Verify(w => w.EnqueueVolumeSnapshotCreateAsync(
            It.IsAny<Guid>(), volume.Id, "cs-vol-1", Project, volume.ClusterId,
            It.IsAny<CancellationToken>()), Times.Once);
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
        _snapshotRepo.Setup(r => r.CountActiveByVolumeIdAsync(volume.Id, default)).ReturnsAsync(0);

        var result = await CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-attached"), default);

        // titan-ops architecture/http-contract.md#create-returns-id: status is asserted on the persisted snapshot, not on the response.
        _snapshotRepo.Verify(r => r.SaveAsync(
            It.Is<VolumeSnapshot>(x => x.Id == result.SnapshotId
                                    && x.Transition == VolumeSnapshotTransition.Creating), default), Times.Once);
    }

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        _snapshotRepo.Setup(r => r.CountActiveByVolumeIdAsync(volume.Id, default)).ReturnsAsync(3);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("snap-over"), default));

        Assert.Equal("VOLUME_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
        _commands.Verify(w => w.EnqueueVolumeSnapshotCreateAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
        _volumeRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Volume?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(Guid.NewGuid(), new CreateVolumeSnapshotBody("snap"), default));

        Assert.Equal("VOLUME_NOT_FOUND", ex.Code);
    }

    // ── DeleteAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_SnapshotOnIncus_Success_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Exists, csSnapId: "cs-snap-1");
        SetupSnapshot(snapshot);

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(VolumeSnapshotTransition.Deleting, snapshot.Transition);
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            snapshot.Id, volume.Id, "cs-snap-1", Project, volume.ClusterId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_SnapshotCreating_ThrowsSnapshotInvalidState()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Creating);
        SetupSnapshot(snapshot);

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
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Creating);
        SetupSnapshot(snapshot);
        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id, new UpdateVolumeSnapshotStatusBody("Created", ExternalId: "cs-snap-9"), default);

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(VolumeSnapshotTransition.Deleting, snapshot.Transition);
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            snapshot.Id, volume.Id, "cs-snap-9", Project, volume.ClusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory] // titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat: the aggregate refuses a repeat; the handler recognises it and answers quietly
    [InlineData(SnapSeed.Deleting)]
    [InlineData(SnapSeed.Deleted)]
    public async Task Delete_RepeatedOrDone_IsANoOp_EnqueuesNoCommand(SnapSeed from)
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume.Id, from, csSnapId: "cs-snap-1");
        SetupSnapshot(snapshot);
        var before = (snapshot.CurrentExistence, snapshot.DesiredExistence, snapshot.PendingIntentId);

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(before, (snapshot.CurrentExistence, snapshot.DesiredExistence, snapshot.PendingIntentId));
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_SnapshotNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        _snapshotRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((VolumeSnapshot?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(volume.Id, Guid.NewGuid(), default));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    // ── RestoreAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_SnapshotOnIncus_CreatesNewVolumeAndEnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Exists, csSnapId: "cs-snap-1");
        SetupSnapshot(snapshot);
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(5);

        var result = await CreateService().RestoreAsync(volume.Id, snapshot.Id,
            new RestoreVolumeSnapshotBody("vol-restored-01"), default);

        Assert.NotEqual(Guid.Empty, result.NewVolumeId);
        _volumeRepo.Verify(r => r.Add(It.Is<Volume>(v => v.Name == "vol-restored-01" && v.ClusterId == volume.ClusterId)), Times.Once);
        _volumeRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVolumeSnapshotRestoreAsync(
            snapshot.Id, result.NewVolumeId, "cs-snap-1", "vol-restored-01", "t-" + _tenant.Id.ToString("N"), volume.ClusterId,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Restore_OriginalVolumeDeleted_StillSucceeds()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Deleted, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Exists, csSnapId: "cs-snap-1");
        SetupSnapshot(snapshot);
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
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Creating);
        SetupSnapshot(snapshot);

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
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Exists, csSnapId: "cs-snap-1");
        SetupSnapshot(snapshot);
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(10);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RestoreAsync(volume.Id, snapshot.Id,
                new RestoreVolumeSnapshotBody("vol-restore"), default));

        Assert.Equal("VOLUME_QUOTA_EXCEEDED", ex.Code);
    }

    // ── Callbacks (titan-ops features/volume/design.md#volume-reports) ──────────────

    [Fact] // Incus made the snapshot before the create command answered: its id is the create done
    public async Task UpdateStatus_Created_StoresTheId_AndEndsTheCreate()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Creating);
        SetupSnapshot(snapshot);

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id, new UpdateVolumeSnapshotStatusBody("Created", ExternalId: "vol1/s1"), default);

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vol1/s1", snapshot.ExternalId);
        _snapshotRepo.Verify(r => r.SaveAsync(snapshot, default), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_RequestFailed_OnAWaitingCreate_RecordsWhy()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Creating);
        SetupSnapshot(snapshot);

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id,
            new UpdateVolumeSnapshotStatusBody("RequestFailed", Reason: "Snapshot creation failed.", Detail: "raw"), default);

        Assert.Equal("NotCreated/Exists/-", Shape(snapshot));
        Assert.Equal("Snapshot creation failed.", snapshot.RequestFailureReason);
        Assert.Equal("raw", snapshot.RequestFailureDetail);
    }

    // The give-up found the snapshot on Incus: the create happened, and the delete reaches Incus.
    [Fact]
    public async Task UpdateStatus_RequestFailedCarryingTheId_IsTheCreateDone()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Creating);
        SetupSnapshot(snapshot);

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id,
            new UpdateVolumeSnapshotStatusBody("RequestFailed", ExternalId: "vol1/s1", Reason: "Snapshot creation failed."), default);

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Null(snapshot.RequestFailureReason);
        _activity.Verify(a => a.StageOutcome(TenantId, "volume_snapshot.created", "volume_snapshot", snapshot.Id, "snap-01"), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_RequestFailed_WhileADeleteWaits_GivesTheSnapshotBack_WithTheReason_AndItCanBeDeletedAgain()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available, csId: "cs-vol-1");
        SetupVolume(volume);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Deleting, csSnapId: "cs-snap-1");
        SetupSnapshot(snapshot);

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id,
            new UpdateVolumeSnapshotStatusBody("RequestFailed", Reason: "Snapshot deletion failed.", Detail: "refused"), default);

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("Snapshot deletion failed.", snapshot.RequestFailureReason);

        await CreateService().DeleteAsync(volume.Id, snapshot.Id, default);

        Assert.Equal(VolumeSnapshotTransition.Deleting, snapshot.Transition);
        _commands.Verify(w => w.EnqueueVolumeSnapshotDeleteAsync(
            snapshot.Id, volume.Id, "cs-snap-1", Project, volume.ClusterId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateStatus_Deleted_EndsTheDelete()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Deleting);
        SetupSnapshot(snapshot);

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id, new UpdateVolumeSnapshotStatusBody("Deleted"), default);

        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
    }

    // Only the delete command ends a delete: the reconciler not finding the snapshot is NotFound.
    [Fact]
    public async Task UpdateStatus_NotFound_WhileADeleteWaits_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Deleting);
        SetupSnapshot(snapshot);

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id, new UpdateVolumeSnapshotStatusBody("Observed", Exists: false), default);

        Assert.Equal("NotFound/Deleted/Deleting", Shape(snapshot));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_NotFound_OnASnapshotStillWanted_IsNotFound_AndSeenAgainExists()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Exists);
        SetupSnapshot(snapshot);

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id, new UpdateVolumeSnapshotStatusBody("Observed", Exists: false), default);
        Assert.Equal("NotFound/Exists/-", Shape(snapshot));

        await CreateService().UpdateStatusAsync(volume.Id, snapshot.Id, new UpdateVolumeSnapshotStatusBody("Observed", Exists: true), default);
        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_SnapshotNotFound_ThrowsNotFoundException()
    {
        _snapshotRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((VolumeSnapshot?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateVolumeSnapshotStatusBody("Deleted"), default));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    [Theory] // reports the worker never sends, the old words included
    [InlineData("BackedUp")]
    [InlineData("Absent")]
    [InlineData("Error")]
    [InlineData("Creating")]
    [InlineData("Frobnicated")]
    public async Task UpdateStatus_UnknownReport_Is400(string report)
    {
        var volume = MakeVolume(VolSeed.Available);
        var snapshot = MakeSnapshot(volume.Id, SnapSeed.Exists);
        SetupSnapshot(snapshot);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(volume.Id, snapshot.Id, new UpdateVolumeSnapshotStatusBody(report), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _snapshotRepo.Verify(r => r.SaveAsync(It.IsAny<VolumeSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_ObservedWithoutExists_Is400()
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateVolumeSnapshotStatusBody("Observed"), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    // ── Activity: one request, one outcome (titan-ops features/activity/design.md#request-and-outcome-names) ─────

    [Fact]
    public async Task Create_RecordsTheRequest_AndTheCallbackRecordsTheOutcomeOnce()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        _snapshotRepo.Setup(r => r.CountActiveByVolumeIdAsync(volume.Id, default)).ReturnsAsync(0);
        VolumeSnapshot? saved = null;
        _snapshotRepo.Setup(r => r.SaveAsync(It.IsAny<VolumeSnapshot>(), default)).Callback<VolumeSnapshot, CancellationToken>((sn, _) => saved = sn);

        var result = await CreateService().CreateAsync(volume.Id, new CreateVolumeSnapshotBody("nightly"), default);

        _activity.Verify(a => a.Stage("volume_snapshot.create_requested", "volume_snapshot", result.SnapshotId, "nightly"), Times.Once);

        Assert.NotNull(saved);
        SetupSnapshot(saved!);
        await CreateService().UpdateStatusAsync(volume.Id, saved!.Id, new UpdateVolumeSnapshotStatusBody("Created", ExternalId: "cs-snap-1"), default);
        await CreateService().UpdateStatusAsync(volume.Id, saved.Id, new UpdateVolumeSnapshotStatusBody("Observed", Exists: true), default);
        await CreateService().UpdateStatusAsync(volume.Id, saved.Id, new UpdateVolumeSnapshotStatusBody("Created", ExternalId: "cs-snap-1"), default);

        _activity.Verify(a => a.StageOutcome(saved.TenantId, "volume_snapshot.created", "volume_snapshot", saved.Id, "nightly"), Times.Once);
    }

    [Fact]
    public async Task Delete_RecordsTheRequest_AndTheCallbackRecordsTheOutcomeOnce()
    {
        SetupCurrentUser(TenantId);
        var volume = MakeVolume(VolSeed.Available);
        SetupVolume(volume);
        var snap = MakeSnapshot(volume.Id, SnapSeed.Exists);
        SetupSnapshot(snap);

        await CreateService().DeleteAsync(volume.Id, snap.Id, default);
        await CreateService().DeleteAsync(volume.Id, snap.Id, default); // repeat while deleting: nothing more

        _activity.Verify(a => a.Stage("volume_snapshot.delete_requested", "volume_snapshot", snap.Id, "snap-01"), Times.Once);

        await CreateService().UpdateStatusAsync(volume.Id, snap.Id, new UpdateVolumeSnapshotStatusBody("Deleted"), default);
        await CreateService().UpdateStatusAsync(volume.Id, snap.Id, new UpdateVolumeSnapshotStatusBody("Deleted"), default);

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
        var snap = MakeSnapshot(source.Id, SnapSeed.Exists);
        SetupSnapshot(snap);
        _volumeRepo.Setup(r => r.CountActiveByTenantAsync(TenantId, default)).ReturnsAsync(0);

        var result = await CreateService().RestoreAsync(source.Id, snap.Id, new RestoreVolumeSnapshotBody("restored-01"), default);

        _activity.Verify(a => a.Stage("volume_snapshot.restore_requested", "volume", result.NewVolumeId, "restored-01"), Times.Once);
    }

    [Fact]
    public async Task FailedCallback_RecordsNoOutcome()
    {
        var volume = MakeVolume(VolSeed.Available);
        var snap = MakeSnapshot(volume.Id, SnapSeed.Creating);
        SetupSnapshot(snap);

        await CreateService().UpdateStatusAsync(volume.Id, snap.Id, new UpdateVolumeSnapshotStatusBody("RequestFailed", Reason: "boom"), default);

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
        var snap = MakeSnapshot(volume.Id, SnapSeed.CreateFailed);
        SetupSnapshot(snap);

        await CreateService().DeleteAsync(volume.Id, snap.Id, default);

        Assert.Equal("Deleted/Deleted/-", Shape(snap));
        Assert.Empty(_commands.Invocations);
        _activity.Verify(a => a.StageOutcome(TenantId, "volume_snapshot.deleted", "volume_snapshot", snap.Id, "snap-01"), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    private void SetupVolume(Volume volume) =>
        _volumeRepo.Setup(r => r.FindByIdAsync(volume.Id, default)).ReturnsAsync(volume);

    private void SetupSnapshot(VolumeSnapshot snapshot) =>
        _snapshotRepo.Setup(r => r.FindByIdAsync(snapshot.Id, default)).ReturnsAsync(snapshot);

    private static string Shape(VolumeSnapshot s)
    {
        string transition = "-";
        if (s.Transition != null)
        {
            transition = s.Transition.Value.ToString();
        }
        return $"{s.CurrentExistence}/{s.DesiredExistence}/{transition}";
    }

    // Situations named in the tests' vocabulary: Attaching/Detaching/Deleting mean "that request is waiting".
    public enum VolSeed { Creating, Available, Attaching, Attached, Detaching, Deleting, Deleted, NotFound }
    public enum SnapSeed { Creating, CreateFailed, Exists, Deleting, Deleted }

    private static Volume MakeVolume(VolSeed status, string? csId = null)
    {
        var vol = Volume.CreateUnchecked(TenantId, "vol-01", "cluster-1", "offering-1", 50);
        var effectiveId = csId ?? "cs-vol-default";
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-1", 20);
        vm.MarkRunning("cs-vm-1", "10.0.0.5");
        switch (status)
        {
            case VolSeed.Available: vol.MarkCreated(effectiveId); break;
            case VolSeed.Attaching: vol.MarkCreated(effectiveId); vol.Attach(vm); break;
            case VolSeed.Attached: vol.MarkCreated(effectiveId); vol.Attach(vm); vol.MarkObserved(vm.Id, false); break;
            case VolSeed.Detaching: vol.MarkCreated(effectiveId); vol.Attach(vm); vol.MarkObserved(vm.Id, false); vol.Detach(); break;
            case VolSeed.Deleting: vol.MarkCreated(effectiveId); vol.RequestDelete(liveSnapshotCount: 0); break;
            case VolSeed.Deleted: vol.MarkCreated(effectiveId); vol.RequestDelete(liveSnapshotCount: 0); vol.MarkDeleted(); break;
            case VolSeed.NotFound: vol.MarkCreated(effectiveId); vol.MarkNotFound(); break;
        }
        return vol;
    }

    private static VolumeSnapshot MakeSnapshot(Guid volumeId, SnapSeed status, string? csSnapId = null)
    {
        var snap = VolumeSnapshot.CreateUnchecked(volumeId, TenantId, "snap-01"); // seed: fixed volumeId
        switch (status)
        {
            case SnapSeed.CreateFailed: snap.MarkRequestFailed("could not create", "raw"); break;
            case SnapSeed.Exists: snap.MarkCreated(csSnapId ?? "cs-snap-default"); break;
            case SnapSeed.Deleting: snap.MarkCreated(csSnapId ?? "cs-snap-default"); snap.RequestDelete(); break;
            case SnapSeed.Deleted: snap.MarkCreated("x"); snap.RequestDelete(); snap.MarkDeleted(); break;
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
        Assert.NotNull(typeof(VolumeSnapshotQueryResult).GetProperty("CurrentExistence"));
    }
}

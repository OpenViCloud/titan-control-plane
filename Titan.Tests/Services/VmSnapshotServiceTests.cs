using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Queries.Compute.VmSnapshot.Contracts;
using VmSnapCmd = Titan.Application.Commands.Compute.VmSnapshot;
using VmSnapQry = Titan.Application.Queries.Compute.VmSnapshot;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using Titan.Domain.Repositories;

using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// A snapshot is written through its VM (titan-ops architecture/code-structure.md#child-entity): the create, revert, delete
// and callback handlers load the VM with its row locked, change it and save it through IVmRepository. The snapshot repository is read side only, so only the list query asks it.
public class VmSnapshotServiceTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;
    // The tenant's Incus project, as titan-ops architecture/tenancy-and-admin.md#project-name spells it.
    private static string Project => "t-" + TenantId.ToString("N");

    private readonly Mock<IVmRepository> _vmRepo = new();
    private readonly Mock<IVmSnapshotRepository> _snapshotRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IActivityRecorder> _activity = new();

    // titan-ops architecture/tenancy-and-admin.md#tenant-quota — the handlers load the tenant to enforce its
    // quota; Tenant.Create's default caps (3 snapshots per VM) are what the quota assertions in this file count against.
    public VmSnapshotServiceTests()
        => _tenantRepo
            .Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestTenant.Active("t", "kc"));

    // Facade over the snapshot handlers so the tests keep one call shape; it forwards and adds
    // nothing.
    private VmSnapshotFacade CreateService() => new(
        _vmRepo.Object, _snapshotRepo.Object, _tenantRepo.Object, _currentUser.Object, _commands.Object, _activity.Object);

    private sealed class VmSnapshotFacade(
        IVmRepository vmRepo,
        IVmSnapshotRepository snapshotRepo,
        ITenantRepository tenantRepo,
        ICurrentUser currentUser,
        IProviderCommandQueue commands,
        IActivityRecorder activity)
    {
        public Task<CreateVmSnapshotCommandResult> CreateAsync(Guid vmId, CreateSnapshotBody body, CancellationToken ct = default)
            => new VmSnapCmd.CreateVmSnapshotCommandHandler(vmRepo, tenantRepo, currentUser, commands, activity)
                .HandleAsync(new CreateVmSnapshotCommand(vmId, body.Name), ct);

        public Task<List<VmSnapshotQueryResult>> ListAsync(Guid vmId, CancellationToken ct = default)
            => new VmSnapQry.ListVmSnapshotsQueryHandler(vmRepo, snapshotRepo, currentUser)
                .HandleAsync(new ListVmSnapshotsQuery(vmId), ct);

        public Task RevertAsync(Guid vmId, Guid snapshotId, CancellationToken ct = default)
            => new VmSnapCmd.RevertVmSnapshotCommandHandler(vmRepo, currentUser, commands, activity)
                .HandleAsync(new RevertVmSnapshotCommand(vmId, snapshotId), ct);

        public Task DeleteAsync(Guid vmId, Guid snapshotId, CancellationToken ct = default)
            => new VmSnapCmd.DeleteVmSnapshotCommandHandler(vmRepo, currentUser, commands, activity)
                .HandleAsync(new DeleteVmSnapshotCommand(vmId, snapshotId), ct);

        public Task EndRequestAsync(Guid vmId, Guid snapshotId, Guid requestId, string outcome, string? externalId = null,
            string? reason = null, string? detail = null, CancellationToken ct = default)
            => new VmSnapCmd.EndVmSnapshotRequestCommandHandler(vmRepo, activity)
                .HandleAsync(new EndVmSnapshotRequestCommand(vmId, snapshotId, requestId, outcome, externalId, reason, detail), ct);

        public Task ObserveAsync(Guid vmId, Guid snapshotId, bool exists, CancellationToken ct = default)
            => new VmSnapCmd.ObserveVmSnapshotCommandHandler(vmRepo)
                .HandleAsync(new ObserveVmSnapshotCommand(vmId, snapshotId, exists), ct);
    }

    // Test-local request bodies; the facade unpacks them into commands.
    private record CreateSnapshotBody(string Name);

    // The report of the request the snapshot waits on now, as its command sends it.
    private Task EndWaiting(Vm vm, VmSnapshot snap, string outcome, string? externalId = null, string? reason = null,
        string? detail = null)
        => CreateService().EndRequestAsync(vm.Id, snap.Id, snap.Operation.PendingId!.Value, outcome, externalId, reason, detail);

    // ── Create success ───────────────────────────────────────

    [Fact]
    public async Task Create_Success_ReturnsCreatingDto_AndEnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());

        var result = await CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("before-upgrade"), default);

        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so the
        // status and name are asserted on the snapshot the VM now holds, saved with it.
        Assert.NotEqual(Guid.Empty, result.SnapshotId);
        VmSnapshot created = vm.Snapshots.Single(s => s.Id == result.SnapshotId);
        Assert.Equal("Creating/-", Shape(created));
        Assert.Equal("before-upgrade", created.Name);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotCreateAsync(
            It.Is<global::Titan.Domain.Aggregates.Vm.Vm>(a54 => a54.Id == vm.Id && a54.ClusterId == "cluster-1"), It.Is<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(a55 => a55.Id == result.SnapshotId && a55.Name == "before-upgrade"), created.Operation.PendingId!.Value, vm.ExternalId!, Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The cap is counted on the snapshots loaded with the VM, under a lock on its row: the handler must take the
    // locked read, not the plain one (titan-ops architecture/code-structure.md#child-entity).
    [Fact]
    public async Task Create_LoadsTheVmForUpdate_NotTheUnlockedRead()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, default)).ReturnsAsync(vm);

        var result = await CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("locked"), default);

        Assert.Contains(vm.Snapshots, s => s.Id == result.SnapshotId);
        _vmRepo.Verify(r => r.FindByIdForUpdateAsync(vm.Id, default), Times.Once);
        _vmRepo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── VM in a status Incus does not snapshot ───────────────

    [Fact]
    public async Task Create_VmInError_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeVmWithStatus(VmStatus.Error));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Empty(vm.Snapshots);
        _commands.Verify(w => w.EnqueueVmSnapshotCreateAsync(
            It.IsAny<global::Titan.Domain.Aggregates.Vm.Vm>(), It.IsAny<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Quota exceeded ───────────────────────────────────────

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        for (int i = 0; i < 3; i++)
        {
            vm.WithSnapshot($"s{i}").MarkCreated($"cs-vm-id/s{i}");
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
        Assert.Equal(3, vm.Snapshots.Count);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    // The handler asks about quota before the VM checks itself (titan-ops architecture/code-structure.md#quota-gate).
    [Fact]
    public async Task Create_AtTheCap_OnAVmNotSnapshottable_AnswersTheQuota()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        for (int i = 0; i < 3; i++)
        {
            vm.WithSnapshot($"s{i}").MarkCreated($"cs-vm-id/s{i}");
        }
        vm.MarkObserved(VmStatus.Error, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
    }

    // A second create while the first still runs: the VM is Snapshotting, so it answers VM_INVALID_STATUS below the cap
    // and the quota once the cap is reached (the gate is asked first, titan-ops architecture/code-structure.md#quota-gate).
    [Fact]
    public async Task Create_WhileTheVmIsSnapshotting_IsRefused_BelowTheCap_AndAnswersTheQuotaAtIt()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        vm.WithSnapshot("running"); // its create still waits: the VM is Snapshotting

        var below = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("second"), default));
        Assert.Equal("VM_INVALID_STATUS", below.Code);
        Assert.Single(vm.Snapshots);

        vm.Snapshots.Single().MarkCreated("cs-vm-id/s0");
        vm.WithSnapshot("a").MarkCreated("cs-vm-id/s1");
        vm.WithSnapshot("b"); // three counted, the last still creating
        var atCap = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("fourth"), default));
        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", atCap.Code);
    }

    // ── What does not count toward the cap ──────────────────

    [Fact]
    public async Task Create_TwoReadyOneFailed_QuotaNotExceeded_Succeeds()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        vm.WithSnapshot("a").MarkCreated("cs-vm-id/a");
        vm.WithSnapshot("b").MarkCreated("cs-vm-id/b");
        vm.WithSnapshot("failed").MarkRequestFailed("could not create", "raw");

        var result = await CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default);

        Assert.Equal(VmSnapshotStatus.Creating, vm.Snapshots.Single(s => s.Id == result.SnapshotId).Status);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact] // a delete pressed on one of three stops counting it at once (DCS-Q1)
    public async Task Create_AtTheCapWithOneSnapshotBeingDeleted_IsAccepted()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        for (int i = 0; i < 3; i++)
        {
            vm.WithSnapshot($"s{i}").MarkCreated($"cs-vm-id/s{i}");
        }
        vm.DeleteSnapshot(vm.Snapshots.First().Id, Guid.NewGuid());

        var result = await CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("fourth"), default);

        Assert.Contains(vm.Snapshots, s => s.Id == result.SnapshotId);
        Assert.Equal(4, vm.Snapshots.Count);
    }

    [Fact]
    public async Task Create_VmNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        _vmRepo.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Vm?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(Guid.NewGuid(), new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    // ── Tenant isolation ─────────────────────────────────────

    [Fact]
    public async Task Create_VmBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeVmForTenant(TestTenant.WithRoomToSpare()));

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Create_NoTenantContext_ThrowsForbiddenException()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateService().CreateAsync(Guid.NewGuid(), new CreateSnapshotBody("test"), default));
    }

    // ── List: the read side, asked through the snapshot repository ───────────────

    [Fact]
    public async Task List_ReturnsNonDeletedSnapshots()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var ready = vm.WithSnapshot("ready");
        ready.MarkCreated("cs-vm-id/ready");
        var creating = vm.WithSnapshot("creating");
        _snapshotRepo.Setup(r => r.FindByVmIdAsync(vm.Id, default)).ReturnsAsync([ready, creating]);

        var result = await CreateService().ListAsync(vm.Id, default);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Id == ready.Id && s.Status == VmSnapshotStatus.Available);
        Assert.Contains(result, s => s.Id == creating.Id && s.Status == VmSnapshotStatus.Creating);
    }

    [Fact]
    public async Task List_Empty_ReturnsEmpty()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        _snapshotRepo.Setup(r => r.FindByVmIdAsync(vm.Id, default)).ReturnsAsync([]);

        var result = await CreateService().ListAsync(vm.Id, default);

        Assert.Empty(result);
    }

    // ── Revert success ───────────────────────────────────────

    [Fact]
    public async Task Revert_Success_StatusReverting_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().RevertAsync(vm.Id, snap.Id, default);

        Assert.Equal("Reverting/Available", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotRevertAsync(
            It.Is<global::Titan.Domain.Aggregates.Vm.Vm>(a58 => a58.Id == vm.Id && a58.ClusterId == "cluster-1"), It.Is<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(a59 => a59.Id == snap.Id), snap.Operation.PendingId!.Value, snap.ExternalId!, Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Revert snapshot not Ready ────────────────────────────

    [Fact]
    public async Task Revert_SnapshotNotReady_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RevertAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    // The VM's own rule comes first: a snapshot being created makes the VM Snapshotting.
    [Fact]
    public async Task Revert_WhileASnapshotIsBeingCreated_ThrowsInvalidStatus()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RevertAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    // ── Revert VM not Running ───────────────────────────────

    [Fact]
    public async Task Revert_VmInError_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeVmWithStatus(VmStatus.Running));
        var snap = MakeSnapshot(vm, Snap.Ready);
        vm.MarkObserved(VmStatus.Error, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RevertAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Equal("Available/Available", Shape(snap));
    }

    [Fact]
    public async Task Revert_SnapshotNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().RevertAsync(vm.Id, Guid.NewGuid(), default));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    // A snapshot of another VM reached through this VM's route: the VM does not hold it, so it is not found
    // (titan-ops architecture/code-structure.md#child-entity).
    [Fact]
    public async Task Revert_SnapshotBelongsToOtherVm_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var other = MakeRunningVm();
        var snap = MakeSnapshot(other, Snap.Ready);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().RevertAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
        Assert.Equal("Available/Available", Shape(snap));
    }

    // ── Delete success ───────────────────────────────────────

    [Fact]
    public async Task Delete_Success_StatusDeleting_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().DeleteAsync(vm.Id, snap.Id, default);

        Assert.Equal("Deleting/Available", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(
            It.Is<global::Titan.Domain.Aggregates.Vm.Vm>(a60 => a60.Id == vm.Id && a60.ClusterId == "cluster-1"), It.Is<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(a61 => a61.Id == snap.Id), snap.Operation.PendingId!.Value, snap.ExternalId!, Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Delete snapshot not Ready ────────────────────────────

    [Fact]
    public async Task Delete_SnapshotNotReady_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().DeleteAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    // ── titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat: the aggregate refuses a repeat; the handler recognises it and answers quietly ──

    [Fact]
    public async Task Revert_WhileReverting_IsANoOp_SavesNothing_EnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.RevertRequested);

        await CreateService().RevertAsync(vm.Id, snap.Id, default);

        Assert.Equal("Reverting/Available", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVmSnapshotRevertAsync(
            It.IsAny<global::Titan.Domain.Aggregates.Vm.Vm>(), It.IsAny<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(Snap.DeleteRequested)]
    [InlineData(Snap.Deleted)]
    public async Task Delete_RepeatedOrDone_IsANoOp_SavesNothing_EnqueuesNoCommand(Snap from)
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, from);
        string before = Shape(snap);

        await CreateService().DeleteAsync(vm.Id, snap.Id, default);

        Assert.Equal(before, Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(
            It.IsAny<global::Titan.Domain.Aggregates.Vm.Vm>(), It.IsAny<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_SnapshotNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(vm.Id, Guid.NewGuid(), default));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Delete_SnapshotOfAnotherVm_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var other = MakeRunningVm();
        var snap = MakeSnapshot(other, Snap.Ready);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
        Assert.Equal("Available/Available", Shape(snap));
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(
            It.IsAny<global::Titan.Domain.Aggregates.Vm.Vm>(), It.IsAny<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Callbacks (titan-ops features/vm/design.md#vm-snapshot-reports) ───────────

    [Fact]
    public async Task Done_OfTheCreate_StoresTheId_AndEndsIt()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        await EndWaiting(vm, snap, "Done", "cs-snap-abc");

        Assert.Equal("Available/Available", Shape(snap));
        Assert.Equal("cs-snap-abc", snap.ExternalId);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact] // the create command gave up before Incus had the snapshot: why, kept for the tenant; the raw detail too
    public async Task Failed_OfAWaitingCreate_RecordsWhy_AndRecordsNoOutcome()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        await EndWaiting(vm, snap, "Failed", reason: "Snapshot creation failed.", detail: "quota exhausted");

        Assert.Equal("Failed/-", Shape(snap));
        Assert.Equal("Snapshot creation failed.", snap.Operation.FailureReason);
        Assert.Equal("quota exhausted", snap.Operation.FailureDetail);
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    // The create gave up after Incus had the snapshot: the create ends Failed with the id kept, so the snapshot can be deleted through Incus; a failure is not the create's outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    [Fact]
    public async Task Failed_CarryingTheId_EndsTheCreateFailed_KeepingTheId_AndRecordsNoOutcome()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        await EndWaiting(vm, snap, "Failed", "cs-snap-abc", reason: "Snapshot creation failed.");

        Assert.Equal("Available/Available", Shape(snap));
        Assert.Equal("Snapshot creation failed.", snap.Operation.FailureReason);
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        vm.DeleteSnapshot(snap.Id, Guid.NewGuid());
        Assert.Equal(VmSnapshotStatus.Deleting, snap.Status);
        Assert.Equal("cs-snap-abc", snap.GetExternalId());
    }

    [Fact]
    public async Task Done_OfTheRevert_EndsIt()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.RevertRequested);

        await EndWaiting(vm, snap, "Done");

        Assert.Equal("Available/Available", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact] // a revert Incus refused ends, with why and its outcome; the snapshot stays
    public async Task Failed_OfAWaitingRevert_EndsIt_WithTheReason_AndRecordsRevertFailed()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.RevertRequested);

        await EndWaiting(vm, snap, "Failed", reason: "Reverting failed.", detail: "raw");

        Assert.Equal("Available/Available", Shape(snap));
        Assert.Equal("Reverting failed.", snap.Operation.FailureReason);
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.revert_failed", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheDelete_EndsIt()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);

        await EndWaiting(vm, snap, "Done");

        Assert.Equal("Deleted/-", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // The VM delete's Done took the snapshot along and recorded its outcome; the snapshot's own delete command then
    // finds it gone and reports its Done too: absorbed, no second outcome (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    [Fact]
    public async Task Done_OfTheDelete_AfterTheVmTookTheSnapshotAlong_IsAbsorbed_AndRecordsNoOutcome()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);
        Guid deleteId = snap.Operation.PendingId!.Value;
        vm.Delete(Guid.NewGuid());
        bool deletion = vm.MarkDeleted();
        Assert.True(deletion);
        Assert.Equal(VmSnapshotStatus.Deleted, snap.Status);

        await CreateService().EndRequestAsync(vm.Id, snap.Id, deleteId, "Done");

        Assert.Equal("Deleted/-", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact] // a Done on a snapshot that waits on nothing changes nothing and records no outcome
    public async Task Done_WhenNothingWaits_ChangesNothing_RecordsNothing()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().EndRequestAsync(vm.Id, snap.Id, Guid.NewGuid(), "Done", "cs-snap-abc");

        Assert.Equal("Available/Available", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact] // no VM by the route's id
    public async Task Report_SnapshotNotFound_ThrowsNotFoundException()
    {
        _vmRepo.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Vm?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().EndRequestAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Done", "cs-snap-abc"));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    [Fact] // the snapshot's VM is not the one the route names
    public async Task Report_SnapshotUnderAnotherVm_ThrowsNotFoundException()
    {
        var vm = Given(MakeRunningVm());
        var other = Given(MakeRunningVm());
        var snap = MakeSnapshot(other, Snap.Creating);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().EndRequestAsync(vm.Id, snap.Id, snap.Operation.PendingId!.Value, "Done", "cs-snap-abc"));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
        Assert.Equal("Creating/-", Shape(snap));
    }

    // ── Activity: one request, one outcome (titan-ops features/activity/design.md#request-and-outcome-names) ──

    [Fact]
    public async Task Create_RecordsTheRequest_AndTheCallbackRecordsTheOutcomeOnce()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());

        var result = await CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("before-upgrade"), default);

        _activity.Verify(a => a.Stage("vm_snapshot.create_requested", "vm_snapshot", result.SnapshotId, "before-upgrade"), Times.Once);

        // The worker reports against the snapshot's own VM.
        Guid createId = vm.Snapshots.Single(s => s.Id == result.SnapshotId).Operation.PendingId!.Value;
        await CreateService().EndRequestAsync(vm.Id, result.SnapshotId, createId, "Done", "cs-snap-1");
        await CreateService().EndRequestAsync(vm.Id, result.SnapshotId, createId, "Done", "cs-snap-1");

        // titan-ops features/activity/design.md#actor-and-tenant: the outcome carries the tenant OF THE RESOURCE (the callback has no caller).
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.created", "vm_snapshot", result.SnapshotId, "before-upgrade"), Times.Once);
    }

    [Fact]
    public async Task Revert_RecordsTheRequest_AndTheCallbackRecordsTheOutcome()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().RevertAsync(vm.Id, snap.Id, default);
        await CreateService().RevertAsync(vm.Id, snap.Id, default); // repeat while reverting: nothing more

        _activity.Verify(a => a.Stage("vm_snapshot.revert_requested", "vm_snapshot", snap.Id, "test-snap"), Times.Once);

        await EndWaiting(vm, snap, "Done");

        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.reverted", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
    }

    [Fact]
    public async Task Delete_RecordsTheRequest_AndTheCallbackRecordsTheOutcome()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().DeleteAsync(vm.Id, snap.Id, default);
        await CreateService().DeleteAsync(vm.Id, snap.Id, default); // repeat while deleting: nothing more

        _activity.Verify(a => a.Stage("vm_snapshot.delete_requested", "vm_snapshot", snap.Id, "test-snap"), Times.Once);

        Guid deleteId = snap.Operation.PendingId!.Value;
        await CreateService().EndRequestAsync(vm.Id, snap.Id, deleteId, "Done");
        await CreateService().EndRequestAsync(vm.Id, snap.Id, deleteId, "Done");

        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.deleted", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
    }

    // A create that failed before Incus had the snapshot leaves the list at once, with no command
    // (titan-ops architecture/desired-and-current.md#delete-locally).
    [Fact]
    public async Task Delete_OfACreateThatFailedBeforeIncusHadIt_DeletesLocally_AndSendsNothing()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.CreateFailed);

        await CreateService().DeleteAsync(vm.Id, snap.Id, default);

        Assert.Equal("Deleted/-", Shape(snap));
        Assert.Empty(_commands.Invocations);
        _activity.Verify(a => a.Stage("vm_snapshot.delete_requested", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.deleted", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // ── The reconciler's reports, and a failed delete ──

    [Fact] // the reconciler seeing the snapshot never completes a revert
    public async Task Observed_DuringARevert_LeavesItWaiting_AndRecordsNoOutcome()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.RevertRequested);

        await CreateService().ObserveAsync(vm.Id, snap.Id, true);

        Assert.Equal("Reverting/Available", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ObservedAgain_OnASnapshotIncusHadLost_IsAvailableAgain()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);
        snap.MarkNotFound();

        await CreateService().ObserveAsync(vm.Id, snap.Id, true);

        Assert.Equal("Available/Available", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // Only the delete command ends a delete: the reconciler not finding the snapshot is only remembered as seen.
    [Fact]
    public async Task NotFound_WhenTheDeleteWasAsked_StaysDeleting_SeenGone()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);

        await CreateService().ObserveAsync(vm.Id, snap.Id, false);

        Assert.Equal("Deleting/-", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NotFound_WhenNobodyAskedForADelete_IsNotFound()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().ObserveAsync(vm.Id, snap.Id, false);

        Assert.Equal("NotFound/-", Shape(snap));
    }

    [Fact] // the failed delete gives the snapshot back, with why; the next delete is a new request
    public async Task Failed_OfAWaitingDelete_GivesTheSnapshotBack_AndTheNextDeleteIsAccepted()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);

        await EndWaiting(vm, snap, "Failed", reason: "Snapshot deletion failed.", detail: "refused");
        Assert.Equal("Available/Available", Shape(snap));
        Assert.Equal("Snapshot deletion failed.", snap.Operation.FailureReason);
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.delete_failed", "vm_snapshot", snap.Id, "test-snap"), Times.Once);

        await CreateService().DeleteAsync(vm.Id, snap.Id, default);

        Assert.Equal("Deleting/Available", Shape(snap));
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(It.Is<global::Titan.Domain.Aggregates.Vm.Vm>(a68 => a68.Id == vm.Id && a68.ClusterId == "cluster-1"), It.Is<global::Titan.Domain.Aggregates.Vm.VmSnapshot>(a69 => a69.Id == snap.Id), snap.Operation.PendingId!.Value, "cs-snap-abc", Project, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    // The VM the repository hands back on every path a handler takes: plain and locked.
    private Vm Given(Vm vm)
    {
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        _vmRepo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return vm;
    }

    private static Vm MakeRunningVm()
    {
        var vm = MakeVmForTenant(TenantIdOwner);
        vm.MarkRunning("cs-vm-id", "10.0.0.1");
        return vm;
    }

    private static Vm MakeVmWithStatus(VmStatus status)
    {
        var vm = MakeVmForTenant(TenantIdOwner);
        switch (status)
        {
            case VmStatus.Running:  vm.MarkRunning("cs-vm-id", "10.0.0.1"); break;
            case VmStatus.Stopped:  vm.MarkRunning("cs-vm-id", "10.0.0.1"); vm.MarkStopped(); break;
            case VmStatus.Error:    vm.MarkRunning("cs-vm-id", "10.0.0.1"); vm.MarkObserved(VmStatus.Error, null); break;
        }
        return vm;
    }

    private static Vm MakeVmForTenant(Titan.Domain.Aggregates.Tenant.Tenant owner) =>
        Vm.Create(owner.Id, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);

    public enum Snap { Creating, CreateFailed, Ready, RevertRequested, DeleteRequested, Deleted }

    // A snapshot of this VM in the given situation, through TestAggregates.WithSnapshot: it is the VM's child entity.
    private static VmSnapshot MakeSnapshot(Vm vm, Snap seed)
    {
        var snap = vm.WithSnapshot("test-snap");
        switch (seed)
        {
            case Snap.Ready:           snap.MarkCreated("cs-snap-abc"); break;
            case Snap.RevertRequested: snap.MarkCreated("cs-snap-abc"); snap.Revert(Guid.NewGuid()); break;
            case Snap.DeleteRequested: snap.MarkCreated("cs-snap-abc"); snap.Delete(Guid.NewGuid()); break;
            case Snap.Deleted:         snap.MarkCreated("cs-snap-abc"); snap.Delete(Guid.NewGuid()); snap.MarkDeleted(); break;
            case Snap.CreateFailed:    snap.MarkRequestFailed("could not create", "raw"); break;
        }
        return snap;
    }

    // The status, then whether Incus was last known to hold the snapshot (InstanceStatus, "-" when not): while an
    // operation waits the status alone does not say it.
    private static string Shape(VmSnapshot s)
    {
        string seen = "-";
        if (s.InstanceStatus != null)
        {
            seen = s.InstanceStatus.Value.ToString();
        }
        return $"{s.Status}/{seen}";
    }

    // titan-ops architecture/desired-and-current.md#request-failure: the raw error is admin-only.
    [Fact]
    public void TenantVmSnapshotQueryResult_HasNoFailureDetailField_AndOneStatus()
    {
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("RequestFailureDetail"));
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("CurrentState"));
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("DesiredState"));
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("CurrentExistence"));
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("DesiredExistence"));
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("Transition"));
        Assert.NotNull(typeof(VmSnapshotQueryResult).GetProperty("RequestFailureReason"));
        Assert.Equal(typeof(VmSnapshotStatus), typeof(VmSnapshotQueryResult).GetProperty("Status")!.PropertyType);
    }
}

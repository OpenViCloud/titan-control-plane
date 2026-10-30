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

namespace Titan.Tests.Services;

// A snapshot is written through its VM (titan-ops architecture/code-structure.md#child-entity): the create, revert, delete
// and callback handlers load the VM (locked, for a create; by snapshot id, for a callback), change it and save it
// through IVmRepository. The snapshot repository is read side only, so only the list query asks it.
public class VmSnapshotServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
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
            .ReturnsAsync(TestTenant.Active("t", "kc", "t@x.io"));

    // Facade over the five snapshot handlers so the tests keep one call shape; it forwards and adds
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

        public Task UpdateStatusAsync(Guid vmId, Guid snapshotId, UpdateSnapshotStatusBody body, CancellationToken ct = default)
            => new VmSnapCmd.UpdateVmSnapshotStatusCommandHandler(vmRepo, activity)
                .HandleAsync(new UpdateVmSnapshotStatusCommand(vmId, snapshotId, body.Report, body.Exists, body.ExternalId,
                    body.Reason, body.Detail), ct);
    }

    // Test-local request bodies; the facade unpacks them into commands.
    private record CreateSnapshotBody(string Name);
    private record UpdateSnapshotStatusBody(
        string Report, bool? Exists = null, string? ExternalId = null, string? Reason = null, string? Detail = null);

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
        VmSnapshot created = vm.RequireSnapshot(result.SnapshotId);
        Assert.Equal("NotCreated/Exists/Creating", Shape(created));
        Assert.Equal("before-upgrade", created.Name);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotCreateAsync(
            result.SnapshotId, vm.Id, vm.ExternalId!, "before-upgrade",
            Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
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

        Assert.NotNull(vm.SnapshotOrNull(result.SnapshotId));
        _vmRepo.Verify(r => r.FindByIdForUpdateAsync(vm.Id, default), Times.Once);
        _vmRepo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── VM in a status Incus does not snapshot ───────────────

    [Fact]
    public async Task Create_VmInError_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeVmWithStatus(VmState.Error));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Empty(vm.Snapshots);
        _commands.Verify(w => w.EnqueueVmSnapshotCreateAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Quota exceeded ───────────────────────────────────────

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        for (int i = 0; i < 3; i++)
        {
            vm.AddSnapshotUnchecked($"s{i}").MarkCreated($"cs-vm-id/s{i}");
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
        Assert.Equal(3, vm.Snapshots.Count);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Never);
    }

    // ── What does not count toward the cap ──────────────────

    [Fact]
    public async Task Create_TwoReadyOneFailed_QuotaNotExceeded_Succeeds()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        vm.AddSnapshotUnchecked("a").MarkCreated("cs-vm-id/a");
        vm.AddSnapshotUnchecked("b").MarkCreated("cs-vm-id/b");
        vm.AddSnapshotUnchecked("failed").MarkRequestFailed("could not create", "raw");

        var result = await CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("test"), default);

        Assert.Equal(VmSnapshotTransition.Creating, vm.RequireSnapshot(result.SnapshotId).Transition);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact] // a delete pressed on one of three stops counting it at once (DCS-Q1)
    public async Task Create_AtTheCapWithOneSnapshotBeingDeleted_IsAccepted()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        for (int i = 0; i < 3; i++)
        {
            vm.AddSnapshotUnchecked($"s{i}").MarkCreated($"cs-vm-id/s{i}");
        }
        vm.RequestSnapshotDelete(vm.Snapshots.First().Id);

        var result = await CreateService().CreateAsync(vm.Id, new CreateSnapshotBody("fourth"), default);

        Assert.NotNull(vm.SnapshotOrNull(result.SnapshotId));
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
        var vm = Given(MakeVmForTenant(Guid.NewGuid()));

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
        var ready = vm.AddSnapshotUnchecked("ready");
        ready.MarkCreated("cs-vm-id/ready");
        var creating = vm.AddSnapshotUnchecked("creating");
        _snapshotRepo.Setup(r => r.FindByVmIdAsync(vm.Id, default)).ReturnsAsync([ready, creating]);

        var result = await CreateService().ListAsync(vm.Id, default);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Id == ready.Id && s.CurrentExistence == CurrentExistence.Exists);
        Assert.Contains(result, s => s.Id == creating.Id && s.Transition == VmSnapshotTransition.Creating);
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

        Assert.Equal("Exists/Exists/Reverting", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotRevertAsync(
            snap.Id, vm.Id, snap.ExternalId!,
            Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Revert snapshot not Ready ────────────────────────────

    [Fact]
    public async Task Revert_SnapshotNotReady_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RevertAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    // ── Revert VM not Running ───────────────────────────────

    [Fact]
    public async Task Revert_VmInError_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeVmWithStatus(VmState.Error));
        var snap = MakeSnapshot(vm, Snap.Ready);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RevertAsync(vm.Id, snap.Id, default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Equal("Exists/Exists/-", Shape(snap));
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
        Assert.Equal("Exists/Exists/-", Shape(snap));
    }

    // ── Delete success ───────────────────────────────────────

    [Fact]
    public async Task Delete_Success_StatusDeleting_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().DeleteAsync(vm.Id, snap.Id, default);

        Assert.Equal("Exists/Deleted/Deleting", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(
            snap.Id, vm.Id, snap.ExternalId!,
            Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
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

        Assert.Equal("Exists/Exists/Reverting", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVmSnapshotRevertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        Assert.Equal("Exists/Exists/-", Shape(snap));
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Callbacks (titan-ops features/vm/design.md#vm-snapshot-reports) ───────────

    [Fact]
    public async Task UpdateStatus_Created_StoresTheId_AndEndsTheCreate()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Equal("cs-snap-abc", snap.ExternalId);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact] // the create command gave up before Incus had the snapshot: why, kept for the tenant; the raw detail too
    public async Task UpdateStatus_RequestFailed_OnAWaitingCreate_RecordsWhy_AndRecordsNoOutcome()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id,
            new UpdateSnapshotStatusBody("RequestFailed", Reason: "Snapshot creation failed.", Detail: "quota exhausted"), default);

        Assert.Equal("NotCreated/Exists/-", Shape(snap));
        Assert.Equal("Snapshot creation failed.", snap.RequestFailureReason);
        Assert.Equal("quota exhausted", snap.RequestFailureDetail);
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    // The create's Created report was lost, but the give-up found the snapshot on Incus: the create happened, and the
    // snapshot can be deleted through Incus.
    [Fact]
    public async Task UpdateStatus_RequestFailedCarryingTheId_IsTheCreateDone()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Creating);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id,
            new UpdateSnapshotStatusBody("RequestFailed", ExternalId: "cs-snap-abc", Reason: "Snapshot creation failed."), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Null(snap.RequestFailureReason);
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.created", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        Assert.Equal("cs-snap-abc", vm.RequestSnapshotDelete(snap.Id));
    }

    [Fact]
    public async Task UpdateStatus_Reverted_EndsTheRevert()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.RevertRequested);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Reverted"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    [Fact] // a revert Incus refused ends, with why; the snapshot stays
    public async Task UpdateStatus_RequestFailed_OnAWaitingRevert_EndsIt_WithTheReason()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.RevertRequested);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id,
            new UpdateSnapshotStatusBody("RequestFailed", Reason: "Reverting failed.", Detail: "raw"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Equal("Reverting failed.", snap.RequestFailureReason);
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_Deleted_EndsTheDelete()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Deleted"), default);

        Assert.Equal("Deleted/Deleted/-", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // The VM's Deleted took the snapshot along and recorded its outcome; the snapshot's own delete command then finds
    // it gone and reports Deleted too: absorbed, no second outcome (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    [Fact]
    public async Task UpdateStatus_Deleted_AfterTheVmTookTheSnapshotAlong_IsAbsorbed_AndRecordsNoOutcome()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);
        vm.RequestDelete();
        VmDeletionOutcome deletion = vm.MarkDeleted();
        Assert.Equal([snap.Id], deletion.SnapshotsWhoseDeleteWasFulfilled.Select(s => s.Id));

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Deleted"), default);

        Assert.Equal("Deleted/Deleted/-", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact] // a repeated Created changes nothing and records no outcome (it may still save the unchanged row)
    public async Task UpdateStatus_CreatedAgain_IsIdempotent_RecordsNothing()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact] // no VM holds the snapshot
    public async Task UpdateStatus_SnapshotNotFound_ThrowsNotFoundException()
    {
        _vmRepo.Setup(r => r.FindBySnapshotIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((Vm?)null);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), Guid.NewGuid(),
                new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
    }

    [Fact] // the snapshot's VM is not the one the route names
    public async Task UpdateStatus_SnapshotUnderAnotherVm_ThrowsNotFoundException()
    {
        var vm = Given(MakeRunningVm());
        var other = Given(MakeRunningVm());
        var snap = MakeSnapshot(other, Snap.Creating);

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(vm.Id, snap.Id,
                new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", ex.Code);
        Assert.Equal("NotCreated/Exists/Creating", Shape(snap));
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
        await CreateService().UpdateStatusAsync(vm.Id, result.SnapshotId, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-1"), default);
        await CreateService().UpdateStatusAsync(vm.Id, result.SnapshotId, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-1"), default);

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

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Reverted"), default);

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

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Deleted"), default);
        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Deleted"), default);

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

        Assert.Equal("Deleted/Deleted/-", Shape(snap));
        Assert.Empty(_commands.Invocations);
        _activity.Verify(a => a.Stage("vm_snapshot.delete_requested", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.deleted", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // ── The reconciler's reports, and a failed delete ──

    [Fact] // the reconciler seeing the snapshot never completes a revert
    public async Task UpdateStatus_Observed_DuringARevert_LeavesItWaiting_AndRecordsNoOutcome()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.RevertRequested);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: true), default);

        Assert.Equal("Exists/Exists/Reverting", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_ObservedAgain_OnASnapshotIncusHadLost_ExistsAgain()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);
        snap.MarkNotFound();

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: true), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        _vmRepo.Verify(r => r.SaveAsync(default), Times.Once);
    }

    // Only the delete command ends a delete: the reconciler not finding the snapshot is NotFound.
    [Fact]
    public async Task UpdateStatus_NotFound_WhenTheDeleteWasAsked_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: false), default);

        Assert.Equal("NotFound/Deleted/Deleting", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_NotFound_WhenNobodyAskedForADelete_IsNotFound()
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: false), default);

        Assert.Equal("NotFound/Exists/-", Shape(snap));
    }

    [Fact] // the failed delete gives the snapshot back, with why; the next delete is a new request
    public async Task UpdateStatus_RequestFailedOnAWaitingDelete_GivesTheSnapshotBack_AndTheNextDeleteIsAccepted()
    {
        SetupCurrentUser(TenantId);
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.DeleteRequested);

        await CreateService().UpdateStatusAsync(vm.Id, snap.Id,
            new UpdateSnapshotStatusBody("RequestFailed", Reason: "Snapshot deletion failed.", Detail: "refused"), default);
        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Equal("Snapshot deletion failed.", snap.RequestFailureReason);

        await CreateService().DeleteAsync(vm.Id, snap.Id, default);

        Assert.Equal("Exists/Deleted/Deleting", Shape(snap));
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(snap.Id, vm.Id, "cs-snap-abc", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory] // reports the worker never sends, the old words included
    [InlineData("Ready")]
    [InlineData("Present")]
    [InlineData("Absent")]
    [InlineData("Error")]
    [InlineData("Banana")]
    public async Task UpdateStatus_UnknownReport_IsRefused(string report)
    {
        var vm = Given(MakeRunningVm());
        var snap = MakeSnapshot(vm, Snap.Ready);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(vm.Id, snap.Id, new UpdateSnapshotStatusBody(report), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _vmRepo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_ObservedWithoutExists_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateSnapshotStatusBody("Observed"), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    // The VM the repository hands back on every path a handler takes: plain, locked, and by any of its snapshots.
    private Vm Given(Vm vm)
    {
        _vmRepo.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        _vmRepo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        _vmRepo.Setup(r => r.FindBySnapshotIdAsync(It.Is<Guid>(id => vm.SnapshotOrNull(id) != null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(vm);
        return vm;
    }

    private static Vm MakeRunningVm()
    {
        var vm = MakeVmForTenant(TenantId);
        vm.MarkRunning("cs-vm-id", "10.0.0.1");
        return vm;
    }

    private static Vm MakeVmWithStatus(VmState status)
    {
        var vm = MakeVmForTenant(TenantId);
        switch (status)
        {
            case VmState.Running:  vm.MarkRunning("cs-vm-id", "10.0.0.1"); break;
            case VmState.Stopped:  vm.MarkRunning("cs-vm-id", "10.0.0.1"); vm.MarkStopped(); break;
            case VmState.Error:    vm.MarkRunning("cs-vm-id", "10.0.0.1"); vm.MarkObserved(VmState.Error, null); break;
        }
        return vm;
    }

    private static Vm MakeVmForTenant(Guid tenantId) =>
        Vm.CreateUnchecked(tenantId, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);

    public enum Snap { Creating, CreateFailed, Ready, RevertRequested, DeleteRequested, Deleted }

    // A snapshot of this VM in the given situation, through the VM's test seam: it is the VM's child entity.
    private static VmSnapshot MakeSnapshot(Vm vm, Snap seed)
    {
        var snap = vm.AddSnapshotUnchecked("test-snap");
        switch (seed)
        {
            case Snap.Ready:           snap.MarkCreated("cs-snap-abc"); break;
            case Snap.RevertRequested: snap.MarkCreated("cs-snap-abc"); snap.Revert(); break;
            case Snap.DeleteRequested: snap.MarkCreated("cs-snap-abc"); snap.RequestDelete(); break;
            case Snap.Deleted:         snap.MarkCreated("cs-snap-abc"); snap.RequestDelete(); snap.MarkDeleted(); break;
            case Snap.CreateFailed:    snap.MarkRequestFailed("could not create", "raw"); break;
        }
        return snap;
    }

    private static string Shape(VmSnapshot s)
    {
        string transition = "-";
        if (s.Transition != null)
        {
            transition = s.Transition.Value.ToString();
        }
        return $"{s.CurrentExistence}/{s.DesiredExistence}/{transition}";
    }

    // titan-ops architecture/desired-and-current.md#request-failure: the raw error is admin-only.
    [Fact]
    public void TenantVmSnapshotQueryResult_HasNoFailureDetailField_NorAStateWord()
    {
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("RequestFailureDetail"));
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("CurrentState"));
        Assert.Null(typeof(VmSnapshotQueryResult).GetProperty("DesiredState"));
        Assert.NotNull(typeof(VmSnapshotQueryResult).GetProperty("RequestFailureReason"));
        Assert.NotNull(typeof(VmSnapshotQueryResult).GetProperty("CurrentExistence"));
    }
}

using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Queries.Compute.VmSnapshot.Contracts;
using VmSnapCmd = Titan.Application.Commands.Compute.VmSnapshot;
using VmSnapQry = Titan.Application.Queries.Compute.VmSnapshot;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using Titan.Domain.Repositories.Compute;

using Titan.Domain.Repositories.Platform;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class VmSnapshotServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    // The tenant's Incus project, as titan-ops architecture/tenancy-and-admin.md#project-name spells it.
    private static string Project => "t-" + TenantId.ToString("N");
    private static readonly Guid VmId = Guid.NewGuid();

    private readonly Mock<IVmRepository> _vmRepo = new();
    private readonly Mock<IVmSnapshotRepository> _snapshotRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IActivityRecorder> _activity = new();

    // titan-ops architecture/tenancy-and-admin.md#tenant-quota — the handlers load the tenant to enforce its
    // quota; Tenant.Create's default caps are what the quota assertions in this file count against.
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
            => new VmSnapCmd.CreateVmSnapshotCommandHandler(vmRepo, snapshotRepo, tenantRepo, currentUser, commands, activity)
                .HandleAsync(new CreateVmSnapshotCommand(vmId, body.Name), ct);

        public Task<List<VmSnapshotQueryResult>> ListAsync(Guid vmId, CancellationToken ct = default)
            => new VmSnapQry.ListVmSnapshotsQueryHandler(vmRepo, snapshotRepo, currentUser)
                .HandleAsync(new ListVmSnapshotsQuery(vmId), ct);

        public Task RevertAsync(Guid vmId, Guid snapshotId, CancellationToken ct = default)
            => new VmSnapCmd.RevertVmSnapshotCommandHandler(vmRepo, snapshotRepo, currentUser, commands, activity)
                .HandleAsync(new RevertVmSnapshotCommand(vmId, snapshotId), ct);

        public Task DeleteAsync(Guid vmId, Guid snapshotId, CancellationToken ct = default)
            => new VmSnapCmd.DeleteVmSnapshotCommandHandler(vmRepo, snapshotRepo, currentUser, commands, activity)
                .HandleAsync(new DeleteVmSnapshotCommand(vmId, snapshotId), ct);

        public Task UpdateStatusAsync(Guid vmId, Guid snapshotId, UpdateSnapshotStatusBody body, CancellationToken ct = default)
            => new VmSnapCmd.UpdateVmSnapshotStatusCommandHandler(snapshotRepo, activity)
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
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.CountActiveByVmIdAsync(VmId, default)).ReturnsAsync(0);

        var result = await CreateService().CreateAsync(VmId,
            new CreateSnapshotBody("before-upgrade"), default);

        // titan-ops architecture/http-contract.md#create-returns-id: the response carries only the id, so the
        // status and name are asserted on the snapshot that was actually persisted.
        Assert.NotEqual(Guid.Empty, result.SnapshotId);
        _snapshotRepo.Verify(r => r.SaveAsync(
            It.Is<VmSnapshot>(x => x.Id == result.SnapshotId
                                && x.CurrentExistence == CurrentExistence.NotCreated
                                && x.Transition == VmSnapshotTransition.Creating
                                && x.Name == "before-upgrade"), default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotCreateAsync(
            It.IsAny<Guid>(), VmId, vm.ExternalId!, "before-upgrade",
            Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── VM in a status Incus does not snapshot ───────────────

    [Fact]
    public async Task Create_VmInError_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVmWithStatus(VmState.Error);
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(VmId, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        _commands.Verify(w => w.EnqueueVmSnapshotCreateAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Quota exceeded ───────────────────────────────────────

    [Fact]
    public async Task Create_QuotaExceeded_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.CountActiveByVmIdAsync(VmId, default)).ReturnsAsync(3);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().CreateAsync(VmId, new CreateSnapshotBody("test"), default));

        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
    }

    // ── Failed does not count quota ──────────────────────────

    [Fact]
    public async Task Create_TwoReadyOneFailed_QuotaNotExceeded_Succeeds()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.CountActiveByVmIdAsync(VmId, default)).ReturnsAsync(2);

        var result = await CreateService().CreateAsync(VmId,
            new CreateSnapshotBody("test"), default);

        // titan-ops architecture/http-contract.md#create-returns-id: status is asserted on the persisted snapshot, not on the response.
        _snapshotRepo.Verify(r => r.SaveAsync(
            It.Is<VmSnapshot>(x => x.Id == result.SnapshotId
                                && x.Transition == VmSnapshotTransition.Creating), default), Times.Once);
    }

    [Fact]
    public async Task Create_VmNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync((Vm?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(VmId, new CreateSnapshotBody("test"), default));
    }

    // ── Tenant isolation ─────────────────────────────────────

    [Fact]
    public async Task Create_VmBelongsToOtherTenant_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVmForTenant(Guid.NewGuid());
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().CreateAsync(VmId, new CreateSnapshotBody("test"), default));
    }

    [Fact]
    public async Task Create_NoTenantContext_ThrowsForbiddenException()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateService().CreateAsync(VmId, new CreateSnapshotBody("test"), default));
    }

    // ── List ───────────────────────────────────

    [Fact]
    public async Task List_ReturnsNonDeletedSnapshots()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.FindByVmIdAsync(VmId, default)).ReturnsAsync(
        [
            MakeSnapshot(Snap.Ready),
            MakeSnapshot(Snap.Creating)
        ]);

        var result = await CreateService().ListAsync(VmId, default);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task List_Empty_ReturnsEmpty()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.FindByVmIdAsync(VmId, default)).ReturnsAsync([]);

        var result = await CreateService().ListAsync(VmId, default);

        Assert.Empty(result);
    }

    // ── Revert success ───────────────────────────────────────

    [Fact]
    public async Task Revert_Success_StatusReverting_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(Snap.Ready);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().RevertAsync(VmId, snap.Id, default);

        Assert.Equal("Exists/Exists/Reverting", Shape(snap));
        _snapshotRepo.Verify(r => r.SaveAsync(snap, default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotRevertAsync(
            snap.Id, VmId, snap.ExternalId!,
            Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Revert snapshot not Ready ────────────────────────────

    [Fact]
    public async Task Revert_SnapshotNotReady_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(Snap.Creating);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RevertAsync(VmId, snap.Id, default));

        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    // ── Revert VM not Running ───────────────────────────────

    [Fact]
    public async Task Revert_VmInError_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeVmWithStatus(VmState.Error);
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(Snap.Ready);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().RevertAsync(VmId, snap.Id, default));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task Revert_SnapshotNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((VmSnapshot?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().RevertAsync(VmId, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Revert_SnapshotBelongsToOtherVm_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = VmSnapshot.CreateUnchecked(MakeRunningVm().Id, TenantId, "test");
        snap.MarkCreated("cs-snap-abc");
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().RevertAsync(VmId, snap.Id, default));
    }

    // ── Delete success ───────────────────────────────────────

    [Fact]
    public async Task Delete_Success_StatusDeleting_EnqueuesTheCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(Snap.Ready);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().DeleteAsync(VmId, snap.Id, default);

        Assert.Equal("Exists/Deleted/Deleting", Shape(snap));
        _snapshotRepo.Verify(r => r.SaveAsync(snap, default), Times.Once);
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(
            snap.Id, VmId, snap.ExternalId!,
            Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Delete snapshot not Ready ────────────────────────────

    [Fact]
    public async Task Delete_SnapshotNotReady_ThrowsBusinessException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(Snap.Creating);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => CreateService().DeleteAsync(VmId, snap.Id, default));

        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
    }

    // ── titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat: the aggregate refuses a repeat; the handler recognises it and answers quietly ──

    [Fact]
    public async Task Revert_WhileReverting_IsANoOp_SavesNothing_EnqueuesNoCommand()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(Snap.RevertRequested);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().RevertAsync(VmId, snap.Id, default);

        Assert.Equal("Exists/Exists/Reverting", Shape(snap));
        _snapshotRepo.Verify(r => r.SaveAsync(It.IsAny<VmSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVmSnapshotRevertAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(Snap.DeleteRequested)]
    [InlineData(Snap.Deleted)]
    public async Task Delete_RepeatedOrDone_IsANoOp_SavesNothing_EnqueuesNoCommand(Snap from)
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(from);
        string before = Shape(snap);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().DeleteAsync(VmId, snap.Id, default);

        Assert.Equal(before, Shape(snap));
        _snapshotRepo.Verify(r => r.SaveAsync(It.IsAny<VmSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_SnapshotNotFound_ThrowsNotFoundException()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((VmSnapshot?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().DeleteAsync(VmId, Guid.NewGuid(), default));
    }

    // ── Callbacks (titan-ops features/vm/design.md#vm-snapshot-reports) ───────────

    [Fact]
    public async Task UpdateStatus_Created_StoresTheId_AndEndsTheCreate()
    {
        var snap = MakeSnapshot(Snap.Creating);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Equal("cs-snap-abc", snap.ExternalId);
        _snapshotRepo.Verify(r => r.SaveAsync(snap, default), Times.Once);
    }

    [Fact] // the create command gave up before Incus had the snapshot: why, kept for the tenant; the raw detail too
    public async Task UpdateStatus_RequestFailed_OnAWaitingCreate_RecordsWhy_AndRecordsNoOutcome()
    {
        var snap = MakeSnapshot(Snap.Creating);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id,
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
        var snap = MakeSnapshot(Snap.Creating);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id,
            new UpdateSnapshotStatusBody("RequestFailed", ExternalId: "cs-snap-abc", Reason: "Snapshot creation failed."), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Null(snap.RequestFailureReason);
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.created", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        Assert.Equal("cs-snap-abc", snap.RequestDelete());
    }

    [Fact]
    public async Task UpdateStatus_Reverted_EndsTheRevert()
    {
        var snap = MakeSnapshot(Snap.RevertRequested);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Reverted"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        _snapshotRepo.Verify(r => r.SaveAsync(snap, default), Times.Once);
    }

    [Fact] // a revert Incus refused ends, with why; the snapshot stays
    public async Task UpdateStatus_RequestFailed_OnAWaitingRevert_EndsIt_WithTheReason()
    {
        var snap = MakeSnapshot(Snap.RevertRequested);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id,
            new UpdateSnapshotStatusBody("RequestFailed", Reason: "Reverting failed.", Detail: "raw"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Equal("Reverting failed.", snap.RequestFailureReason);
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_Deleted_EndsTheDelete()
    {
        var snap = MakeSnapshot(Snap.DeleteRequested);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Deleted"), default);

        Assert.Equal("Deleted/Deleted/-", Shape(snap));
        _snapshotRepo.Verify(r => r.SaveAsync(snap, default), Times.Once);
    }

    [Fact] // a repeated Created changes nothing and records no outcome (it may still save the unchanged row)
    public async Task UpdateStatus_CreatedAgain_IsIdempotent_RecordsNothing()
    {
        var snap = MakeSnapshot(Snap.Ready);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_SnapshotNotFound_ThrowsNotFoundException()
    {
        _snapshotRepo.Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), default)).ReturnsAsync((VmSnapshot?)null);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(VmId, Guid.NewGuid(),
                new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default));
    }

    [Fact]
    public async Task UpdateStatus_SnapshotBelongsToOtherVm_ThrowsNotFoundException()
    {
        var snap = VmSnapshot.CreateUnchecked(MakeRunningVm().Id, TenantId, "test");
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await Assert.ThrowsAsync<NotFoundException>(
            () => CreateService().UpdateStatusAsync(VmId, snap.Id,
                new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-abc"), default));
    }

    // ── Activity: one request, one outcome (titan-ops features/activity/design.md#request-and-outcome-names) ──

    [Fact]
    public async Task Create_RecordsTheRequest_AndTheCallbackRecordsTheOutcomeOnce()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.CountActiveByVmIdAsync(VmId, default)).ReturnsAsync(0);
        VmSnapshot? saved = null;
        _snapshotRepo.Setup(r => r.SaveAsync(It.IsAny<VmSnapshot>(), default)).Callback<VmSnapshot, CancellationToken>((sn, _) => saved = sn);

        var result = await CreateService().CreateAsync(VmId, new CreateSnapshotBody("before-upgrade"), default);

        _activity.Verify(a => a.Stage("vm_snapshot.create_requested", "vm_snapshot", result.SnapshotId, "before-upgrade"), Times.Once);

        Assert.NotNull(saved);
        Guid snapshotVmId = saved!.VmId; // the worker reports against the snapshot's own VM
        _snapshotRepo.Setup(r => r.FindByIdAsync(result.SnapshotId, default)).ReturnsAsync(saved);
        await CreateService().UpdateStatusAsync(snapshotVmId, result.SnapshotId, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-1"), default);
        await CreateService().UpdateStatusAsync(snapshotVmId, result.SnapshotId, new UpdateSnapshotStatusBody("Created", ExternalId: "cs-snap-1"), default);

        // titan-ops features/activity/design.md#actor-and-tenant: the outcome carries the tenant OF THE RESOURCE (the callback has no caller).
        _activity.Verify(a => a.StageOutcome(saved.TenantId, "vm_snapshot.created", "vm_snapshot", result.SnapshotId, "before-upgrade"), Times.Once);
    }

    [Fact]
    public async Task Revert_RecordsTheRequest_AndTheCallbackRecordsTheOutcome()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        var snap = MakeSnapshot(Snap.Ready);
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().RevertAsync(VmId, snap.Id, default);
        await CreateService().RevertAsync(VmId, snap.Id, default); // repeat while reverting: nothing more

        _activity.Verify(a => a.Stage("vm_snapshot.revert_requested", "vm_snapshot", snap.Id, "test-snap"), Times.Once);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Reverted"), default);

        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.reverted", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
    }

    [Fact]
    public async Task Delete_RecordsTheRequest_AndTheCallbackRecordsTheOutcome()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        var snap = MakeSnapshot(Snap.Ready);
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().DeleteAsync(VmId, snap.Id, default);
        await CreateService().DeleteAsync(VmId, snap.Id, default); // repeat while deleting: nothing more

        _activity.Verify(a => a.Stage("vm_snapshot.delete_requested", "vm_snapshot", snap.Id, "test-snap"), Times.Once);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Deleted"), default);
        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Deleted"), default);

        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.deleted", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
    }

    // A create that failed before Incus had the snapshot leaves the list at once, with no command
    // (titan-ops architecture/desired-and-current.md#delete-locally).
    [Fact]
    public async Task Delete_OfACreateThatFailedBeforeIncusHadIt_DeletesLocally_AndSendsNothing()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        var snap = MakeSnapshot(Snap.CreateFailed);
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().DeleteAsync(VmId, snap.Id, default);

        Assert.Equal("Deleted/Deleted/-", Shape(snap));
        Assert.Empty(_commands.Invocations);
        _activity.Verify(a => a.Stage("vm_snapshot.delete_requested", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        _activity.Verify(a => a.StageOutcome(TenantId, "vm_snapshot.deleted", "vm_snapshot", snap.Id, "test-snap"), Times.Once);
        _snapshotRepo.Verify(r => r.SaveAsync(snap, default), Times.Once);
    }

    // ── The reconciler's reports, and a failed delete ──

    [Fact] // the reconciler seeing the snapshot never completes a revert
    public async Task UpdateStatus_Observed_DuringARevert_LeavesItWaiting_AndRecordsNoOutcome()
    {
        var snap = MakeSnapshot(Snap.RevertRequested);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: true), default);

        Assert.Equal("Exists/Exists/Reverting", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_ObservedAgain_OnASnapshotIncusHadLost_ExistsAgain()
    {
        var snap = MakeSnapshot(Snap.Ready);
        snap.MarkNotFound();
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: true), default);

        Assert.Equal("Exists/Exists/-", Shape(snap));
        _snapshotRepo.Verify(r => r.SaveAsync(snap, default), Times.Once);
    }

    // Only the delete command ends a delete: the reconciler not finding the snapshot is NotFound.
    [Fact]
    public async Task UpdateStatus_NotFound_WhenTheDeleteWasAsked_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        var snap = MakeSnapshot(Snap.DeleteRequested);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: false), default);

        Assert.Equal("NotFound/Deleted/Deleting", Shape(snap));
        _activity.Verify(a => a.StageOutcome(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_NotFound_WhenNobodyAskedForADelete_IsNotFound()
    {
        var snap = MakeSnapshot(Snap.Ready);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody("Observed", Exists: false), default);

        Assert.Equal("NotFound/Exists/-", Shape(snap));
    }

    [Fact] // the failed delete gives the snapshot back, with why; the next delete is a new request
    public async Task UpdateStatus_RequestFailedOnAWaitingDelete_GivesTheSnapshotBack_AndTheNextDeleteIsAccepted()
    {
        SetupCurrentUser(TenantId);
        var vm = MakeRunningVm();
        _vmRepo.Setup(r => r.FindByIdAsync(VmId, default)).ReturnsAsync(vm);
        var snap = MakeSnapshot(Snap.DeleteRequested);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        await CreateService().UpdateStatusAsync(VmId, snap.Id,
            new UpdateSnapshotStatusBody("RequestFailed", Reason: "Snapshot deletion failed.", Detail: "refused"), default);
        Assert.Equal("Exists/Exists/-", Shape(snap));
        Assert.Equal("Snapshot deletion failed.", snap.RequestFailureReason);

        await CreateService().DeleteAsync(VmId, snap.Id, default);

        Assert.Equal("Exists/Deleted/Deleting", Shape(snap));
        _commands.Verify(w => w.EnqueueVmSnapshotDeleteAsync(snap.Id, VmId, "cs-snap-abc", Project, "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory] // reports the worker never sends, the old words included
    [InlineData("Ready")]
    [InlineData("Present")]
    [InlineData("Absent")]
    [InlineData("Error")]
    [InlineData("Banana")]
    public async Task UpdateStatus_UnknownReport_IsRefused(string report)
    {
        var snap = MakeSnapshot(Snap.Ready);
        _snapshotRepo.Setup(r => r.FindByIdAsync(snap.Id, default)).ReturnsAsync(snap);

        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(VmId, snap.Id, new UpdateSnapshotStatusBody(report), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _snapshotRepo.Verify(r => r.SaveAsync(It.IsAny<VmSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateStatus_ObservedWithoutExists_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<BadRequestException>(
            () => CreateService().UpdateStatusAsync(VmId, Guid.NewGuid(), new UpdateSnapshotStatusBody("Observed"), default));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetupCurrentUser(Guid tenantId) =>
        _currentUser.Setup(u => u.TenantId).Returns(tenantId);

    private Vm MakeRunningVm()
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

    private static VmSnapshot MakeSnapshot(Snap seed)
    {
        var snap = VmSnapshot.CreateUnchecked(VmId, TenantId, "test-snap"); // seed: needs a fixed VmId
        var runningVm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);
        runningVm.MarkRunning("cs-vm-id", "10.0.0.1");
        switch (seed)
        {
            case Snap.Ready:           snap.MarkCreated("cs-snap-abc"); break;
            case Snap.RevertRequested: snap.MarkCreated("cs-snap-abc"); snap.Revert(runningVm); break;
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

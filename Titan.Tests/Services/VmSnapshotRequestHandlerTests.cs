using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The request routes of titan-ops architecture/resource-lifecycle.md#callback-routes for a VM snapshot, at the handler: a
// report ends the request it names and records its outcome once; an observation writes current and ends nothing; the
// snapshot is reached through the VM the route names, with the VM's row locked. The requests' own repeat and lock rules
// are at the end.
public class VmSnapshotRequestHandlerTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    // Only the locked read is set up: a handler that read the VM unlocked would find nothing and answer 404.
    private readonly Mock<IVmRepository> _repo = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly FakeActivityRecorder _activity = new();

    public VmSnapshotRequestHandlerTests() => _currentUser.Setup(u => u.TenantId).Returns(Owner.Id);

    private EndVmSnapshotRequestCommandHandler EndHandler() => new(_repo.Object, _activity);
    private ObserveVmSnapshotCommandHandler ObserveHandler() => new(_repo.Object);
    private RevertVmSnapshotCommandHandler RevertHandler() => new(_repo.Object, _currentUser.Object, _commands.Object, _activity);
    private DeleteVmSnapshotCommandHandler DeleteHandler() => new(_repo.Object, _currentUser.Object, _commands.Object, _activity);

    private Vm Given(Vm vm)
    {
        _repo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return vm;
    }

    private Vm RunningVm()
    {
        Vm vm = Vm.Create(Owner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-1", 20);
        vm.MarkRunning("vm42", "10.0.0.5");
        return Given(vm);
    }

    private static VmSnapshot Creating(Vm vm) => vm.WithSnapshot("snap-1");

    private static VmSnapshot Ready(Vm vm)
    {
        VmSnapshot snapshot = Creating(vm);
        snapshot.MarkCreated("vm42/s1");
        return snapshot;
    }

    private static Guid Waiting(VmSnapshot snapshot) => snapshot.Pending!.Id;

    private IEnumerable<string> Outcomes() => _activity.Events.Where(e => e.OutcomeTenantId != null).Select(e => e.Action);

    private void NothingSaved() => _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);

    private static string Shape(VmSnapshot s) =>
        $"{s.CurrentExistence}/{s.DesiredExistence}/{(s.Transition == null ? "-" : s.Transition.Value.ToString())}";

    private Task End(Vm vm, VmSnapshot snapshot, Guid requestId, string outcome, string? externalId = null) =>
        EndHandler().HandleAsync(new EndVmSnapshotRequestCommand(vm.Id, snapshot.Id, requestId, outcome, externalId,
            outcome == "Failed" ? "The snapshot request could not be completed." : null, outcome == "Failed" ? "raw" : null));

    // ---------- the request report ----------

    [Fact]
    public async Task Done_OfTheCreate_StoresTheId_RecordsVmSnapshotCreatedOnce_AndSaves()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);
        Guid create = Waiting(snapshot);

        await End(vm, snapshot, create, "Done", "vm42/s1");
        await End(vm, snapshot, create, "Done", "vm42/s1");

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.Equal(["vm_snapshot.created"], Outcomes());
        Assert.Equal(Owner.Id, _activity.Events.Single().OutcomeTenantId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Done_OfTheCreate_WithoutTheSnapshotId_Is422_AndSavesNothing(string? externalId)
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        BusinessException refused = await Assert.ThrowsAsync<BusinessException>(
            () => End(vm, snapshot, Waiting(snapshot), "Done", externalId));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.Equal(VmSnapshotTransition.Creating, snapshot.Transition);
        NothingSaved();
    }

    [Fact]
    public async Task Failed_OfTheCreate_RecordsTheReason_KeepsAnIdIncusMade_AndRecordsNoOutcome()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        await End(vm, snapshot, Waiting(snapshot), "Failed", "vm42/s1");

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.Equal("The snapshot request could not be completed.", snapshot.OperationFailureReason);
        Assert.Equal("raw", snapshot.OperationFailureDetail);
        Assert.Empty(Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheRevert_RecordsVmSnapshotRevertedOnce()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        Guid revert = vm.RevertSnapshot(snapshot.Id).OperationId;

        await End(vm, snapshot, revert, "Done");
        await End(vm, snapshot, revert, "Done");

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal(["vm_snapshot.reverted"], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheRevert_RecordsTheReason_AndVmSnapshotRevertFailedOnce()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        Guid revert = vm.RevertSnapshot(snapshot.Id).OperationId;

        await End(vm, snapshot, revert, "Failed");
        await End(vm, snapshot, revert, "Failed");

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.OperationFailureReason);
        Assert.Equal(["vm_snapshot.revert_failed"], Outcomes());
    }

    [Fact]
    public async Task Done_OfTheDelete_MarksTheSnapshotDeleted_AndRecordsVmSnapshotDeletedOnce()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        Guid delete = vm.DeleteSnapshot(snapshot.Id)!.OperationId;

        await End(vm, snapshot, delete, "Done");
        await End(vm, snapshot, delete, "Done");

        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
        Assert.Equal(["vm_snapshot.deleted"], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheDelete_GivesTheSnapshotBack_AndRecordsVmSnapshotDeleteFailedOnce()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        Guid delete = vm.DeleteSnapshot(snapshot.Id)!.OperationId;

        await End(vm, snapshot, delete, "Failed");

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.OperationFailureReason);
        Assert.Equal(["vm_snapshot.delete_failed"], Outcomes());
    }

    [Fact]
    public async Task AReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        vm.RevertSnapshot(snapshot.Id);

        await End(vm, snapshot, Guid.NewGuid(), "Done");
        await End(vm, snapshot, Guid.NewGuid(), "Failed");

        Assert.Equal("Exists/Exists/Reverting", Shape(snapshot));
        Assert.Empty(Outcomes());
        NothingSaved();
    }

    [Fact]
    public async Task AReport_WithAnUnknownOutcome_Is400_BeforeTheVmIsRead()
    {
        BadRequestException refused = await Assert.ThrowsAsync<BadRequestException>(() => EndHandler().HandleAsync(
            new EndVmSnapshotRequestCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Reverted")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_ForAVmThatIsNotThere_IsVmSnapshotNotFound()
    {
        NotFoundException missing = await Assert.ThrowsAsync<NotFoundException>(() => EndHandler().HandleAsync(
            new EndVmSnapshotRequestCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Done", "vm42/s1")));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", missing.Code);
    }

    // A snapshot of another VM reached through this VM's route: the VM does not hold it
    // (titan-ops architecture/code-structure.md#child-entity).
    [Fact]
    public async Task AReport_UnderAnotherVm_IsVmSnapshotNotFound_AndLeavesTheSnapshotWaiting()
    {
        Vm vm = RunningVm();
        Vm other = RunningVm();
        VmSnapshot theirs = Creating(other);

        NotFoundException missing = await Assert.ThrowsAsync<NotFoundException>(() => EndHandler().HandleAsync(
            new EndVmSnapshotRequestCommand(vm.Id, theirs.Id, Waiting(theirs), "Done", "vm42/s1")));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", missing.Code);
        Assert.Equal(VmSnapshotTransition.Creating, theirs.Transition);
        NothingSaved();
    }

    // ---------- the observation ----------

    [Fact]
    public async Task AnObservation_WhileTheCreateWaits_WritesExistence_AndTheCreateKeepsWaiting()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        await ObserveHandler().HandleAsync(new ObserveVmSnapshotCommand(vm.Id, snapshot.Id, true));

        Assert.Equal("Exists/Exists/Creating", Shape(snapshot));
        Assert.Empty(Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnObservation_DuringARevert_LeavesItWaiting()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        vm.RevertSnapshot(snapshot.Id);

        await ObserveHandler().HandleAsync(new ObserveVmSnapshotCommand(vm.Id, snapshot.Id, true));

        Assert.Equal("Exists/Exists/Reverting", Shape(snapshot));
        Assert.Empty(Outcomes());
    }

    [Fact]
    public async Task AnObservation_NotFound_WritesNotFound_AndLeavesTheDeleteWaiting()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        vm.DeleteSnapshot(snapshot.Id);

        await ObserveHandler().HandleAsync(new ObserveVmSnapshotCommand(vm.Id, snapshot.Id, false));

        Assert.Equal("NotFound/Deleted/Deleting", Shape(snapshot));
        Assert.Empty(Outcomes());
    }

    [Fact]
    public async Task AnObservation_UnderAnotherVm_IsVmSnapshotNotFound()
    {
        Vm vm = RunningVm();
        VmSnapshot theirs = Ready(RunningVm());

        NotFoundException missing = await Assert.ThrowsAsync<NotFoundException>(
            () => ObserveHandler().HandleAsync(new ObserveVmSnapshotCommand(vm.Id, theirs.Id, false)));

        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", missing.Code);
        Assert.Equal(CurrentExistence.Exists, theirs.CurrentExistence);
    }

    // ---------- the requests: repeats and the lock ----------

    // A second revert while the first waits answers like the first: no new request, no row, no activity.
    [Fact]
    public async Task Revert_WhileItsRevertWaits_IsANoOp_WithTheSameRequestStillWaiting()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        await RevertHandler().HandleAsync(new RevertVmSnapshotCommand(vm.Id, snapshot.Id));
        Guid first = Waiting(snapshot);

        await RevertHandler().HandleAsync(new RevertVmSnapshotCommand(vm.Id, snapshot.Id));

        Assert.Equal(first, Waiting(snapshot));
        _commands.Verify(c => c.EnqueueVmSnapshotRevertAsync(snapshot.Id, first, vm.Id, "vm42/s1", It.IsAny<string>(),
            "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["vm_snapshot.revert_requested"], _activity.Events.Select(e => e.Action));
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_WhileItsDeleteWaits_IsANoOp_WithTheSameRequestStillWaiting()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        await DeleteHandler().HandleAsync(new DeleteVmSnapshotCommand(vm.Id, snapshot.Id));
        Guid first = Waiting(snapshot);

        await DeleteHandler().HandleAsync(new DeleteVmSnapshotCommand(vm.Id, snapshot.Id));

        Assert.Equal(first, Waiting(snapshot));
        _commands.Verify(c => c.EnqueueVmSnapshotDeleteAsync(snapshot.Id, first, vm.Id, "vm42/s1", It.IsAny<string>(),
            "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["vm_snapshot.delete_requested"], _activity.Events.Select(e => e.Action));
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // A revert after the last one ended is a new request with a new id, so its command gets its own row.
    [Fact]
    public async Task Revert_AfterTheLastOneEnded_IsANewRequest_WithANewId()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        await RevertHandler().HandleAsync(new RevertVmSnapshotCommand(vm.Id, snapshot.Id));
        Guid first = Waiting(snapshot);
        await End(vm, snapshot, first, "Done");

        await RevertHandler().HandleAsync(new RevertVmSnapshotCommand(vm.Id, snapshot.Id));

        Assert.NotEqual(first, Waiting(snapshot));
        _commands.Verify(c => c.EnqueueVmSnapshotRevertAsync(snapshot.Id, Waiting(snapshot), vm.Id, "vm42/s1",
            It.IsAny<string>(), "cluster-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Locked, so a report and a request on the same snapshot take turns
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Theory]
    [InlineData("Revert")]
    [InlineData("Delete")]
    [InlineData("EndVmSnapshotRequest")]
    [InlineData("ObserveVmSnapshot")]
    public async Task EveryHandler_LoadsTheVmLocked(string handler)
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);

        switch (handler)
        {
            case "Revert":
                await RevertHandler().HandleAsync(new RevertVmSnapshotCommand(vm.Id, snapshot.Id));
                break;
            case "Delete":
                await DeleteHandler().HandleAsync(new DeleteVmSnapshotCommand(vm.Id, snapshot.Id));
                break;
            case "EndVmSnapshotRequest":
                await EndHandler().HandleAsync(new EndVmSnapshotRequestCommand(vm.Id, snapshot.Id, Guid.NewGuid(), "Done"));
                break;
            default:
                await ObserveHandler().HandleAsync(new ObserveVmSnapshotCommand(vm.Id, snapshot.Id, true));
                break;
        }

        _repo.Verify(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

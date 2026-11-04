using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants: a VM holds at most one waiting request (id, kind); only the
// report that carries that id ends it; an observation never does; a failed request puts desired back to current.
// VmStateMachineTests keeps what a request may do from each Incus status; this file is about how a request ends.
public class VmRequestLifecycleTests
{
    private static readonly VmObservation Running = new(VmState.Running, "10.0.0.5", null);
    private static readonly VmObservation Stopped = new(VmState.Stopped, null, null);

    private static Vm NewVm() => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

    private static Vm RunningVm()
    {
        Vm vm = NewVm();
        vm.EndOperation(vm.Pending!.Id, VmOperationEnd.Done("42", Running));
        return vm;
    }

    private static VmOperationEnd Failed(string reason = "The VM could not be stopped.", string detail = "raw") =>
        VmOperationEnd.Failed(null, null, reason, detail);

    // ---------- the create ----------

    [Fact]
    public void ANewVm_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        Vm vm = NewVm();

        Assert.NotNull(vm.Pending);
        Assert.Equal(VmOperationKind.Create, vm.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, vm.Pending.Id);
        Assert.True(vm.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(VmTransition.Creating, vm.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdExistenceAndStatus_AndEndsIt()
    {
        Vm vm = NewVm();
        Guid createId = vm.Pending!.Id;

        VmOperationEnded? ended = vm.EndOperation(createId, VmOperationEnd.Done("42", Running));

        Assert.NotNull(ended);
        Assert.Equal(VmOperationKind.Create, ended!.Operation.Kind);
        Assert.Equal(createId, ended.Operation.Id);
        Assert.Null(vm.Pending);
        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal("10.0.0.5", vm.IpAddress);
        Assert.Null(vm.OperationFailureReason);
    }

    [Fact] // the instance was made but never answered: it exists, and its create failed
    public void Failed_OfTheCreate_WithAnId_KeepsTheIdAndExistence_RecordsTheReason_AndKeepsDesired()
    {
        Vm vm = NewVm();

        VmOperationEnded? ended = vm.EndOperation(vm.Pending!.Id, VmOperationEnd.Failed("45", null, "VM provisioning failed.", "raw"));

        Assert.NotNull(ended);
        Assert.Null(vm.Pending);
        Assert.Equal("45", vm.ExternalId);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
        Assert.Equal(DesiredExistence.Exists, vm.DesiredExistence);
        Assert.Equal("VM provisioning failed.", vm.OperationFailureReason);
        Assert.Equal("raw", vm.OperationFailureDetail);
        Assert.NotNull(vm.OperationFailedAt);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_LeavesTheVmNotCreated()
    {
        Vm vm = NewVm();

        vm.EndOperation(vm.Pending!.Id, Failed("VM provisioning failed."));

        Assert.Null(vm.Pending);
        Assert.Null(vm.ExternalId);
        Assert.Equal(CurrentExistence.NotCreated, vm.CurrentExistence);
    }

    // ---------- only the report with the right id ends a request ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsNull()
    {
        Vm vm = RunningVm();
        OperationTicket stop = vm.Stop();
        Guid before = vm.Pending!.Id;

        VmOperationEnded? ended = vm.EndOperation(Guid.NewGuid(), VmOperationEnd.Done(null, Stopped));

        Assert.Null(ended);
        Assert.Equal(before, vm.Pending!.Id);
        Assert.Equal(stop.OperationId, vm.Pending.Id);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
    }

    [Fact]
    public void AReport_WhenNothingWaits_ChangesNothing()
    {
        Vm vm = RunningVm();
        Guid oldId = Guid.NewGuid();

        Assert.Null(vm.EndOperation(oldId, VmOperationEnd.Done(null, Stopped)));
        Assert.Null(vm.EndOperation(oldId, Failed()));

        Assert.Null(vm.Pending);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Null(vm.OperationFailureReason);
    }

    [Fact]
    public void TheSameDone_Twice_EndsTheRequestOnce()
    {
        Vm vm = RunningVm();
        OperationTicket stop = vm.Stop();

        Assert.NotNull(vm.EndOperation(stop.OperationId, VmOperationEnd.Done(null, Stopped)));
        Assert.Null(vm.EndOperation(stop.OperationId, VmOperationEnd.Done(null, Stopped)));
    }

    [Fact] // a give-up re-sent after the customer pressed a new request must not free the new one
    public void AStaleGiveUp_AfterANewRequest_IsIgnored()
    {
        Vm vm = RunningVm();
        OperationTicket firstStop = vm.Stop();
        vm.EndOperation(firstStop.OperationId, Failed());
        OperationTicket secondStop = vm.Stop();

        Assert.Null(vm.EndOperation(firstStop.OperationId, Failed()));

        Assert.Equal(secondStop.OperationId, vm.Pending!.Id);
        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
    }

    [Fact]
    public void ADeletedVm_AbsorbsEveryReportAndObservation()
    {
        Vm vm = RunningVm();
        OperationTicket delete = vm.Delete()!;
        vm.EndOperation(delete.OperationId, VmOperationEnd.Done(null, null));
        Assert.True(vm.IsTerminal);

        vm.MarkObserved(Running);
        Assert.Null(vm.EndOperation(delete.OperationId, VmOperationEnd.Done(null, Running)));
        Assert.Null(vm.EndOperation(Guid.NewGuid(), Failed()));

        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
        Assert.Null(vm.Pending);
    }

    // ---------- Done by kind ----------

    [Fact]
    public void Done_OfAStop_RecordsWhatTheWorkerSaw_AndEndsIt()
    {
        Vm vm = RunningVm();
        OperationTicket stop = vm.Stop();

        VmOperationEnded? ended = vm.EndOperation(stop.OperationId, VmOperationEnd.Done(null, Stopped));

        Assert.Equal(VmOperationKind.Stop, ended!.Operation.Kind);
        Assert.Null(vm.Pending);
        Assert.Equal(VmState.Stopped, vm.CurrentState);
        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
        Assert.Null(vm.Transition);
    }

    [Fact]
    public void Done_OfADelete_EndsTheVm_TakesItsSnapshots_AndNamesTheOnesWhoseDeleteWaited()
    {
        Vm vm = RunningVm();
        VmSnapshotTicket resting = vm.AddSnapshot("resting");
        vm.MarkSnapshotCreated(resting.Snapshot.Id, "s-1");
        VmSnapshotTicket deleting = vm.AddSnapshot("deleting");
        vm.MarkSnapshotCreated(deleting.Snapshot.Id, "s-2");
        vm.DeleteSnapshot(deleting.Snapshot.Id);
        OperationTicket delete = vm.Delete()!;

        VmOperationEnded? ended = vm.EndOperation(delete.OperationId, VmOperationEnd.Done(null, null));

        Assert.Equal(VmOperationKind.Delete, ended!.Operation.Kind);
        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
        Assert.Null(vm.IpAddress);
        Assert.True(vm.IsTerminal);
        Assert.All(vm.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.Equal([deleting.Snapshot.Id], ended.SnapshotsWhoseDeleteEnded.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Done_ClearsAnOlderFailureReason()
    {
        Vm vm = RunningVm();
        OperationTicket firstStop = vm.Stop();
        vm.EndOperation(firstStop.OperationId, Failed());
        Assert.NotNull(vm.OperationFailureReason);
        OperationTicket secondStop = vm.Stop();

        vm.EndOperation(secondStop.OperationId, VmOperationEnd.Done(null, Stopped));

        Assert.Null(vm.OperationFailureReason);
        Assert.Null(vm.OperationFailureDetail);
        Assert.Null(vm.OperationFailedAt);
    }

    // ---------- Failed by kind: desired goes back to current ----------

    [Fact]
    public void Failed_OfAStop_PutsDesiredBackToRunning_AndRecordsTheReason()
    {
        Vm vm = RunningVm();
        OperationTicket stop = vm.Stop();

        VmOperationEnded? ended = vm.EndOperation(stop.OperationId, Failed("The VM could not be stopped."));

        Assert.Equal(VmOperationKind.Stop, ended!.Operation.Kind);
        Assert.Null(vm.Pending);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal("The VM could not be stopped.", vm.OperationFailureReason);
        Assert.Null(vm.Transition);
    }

    [Fact]
    public void Failed_OfAStart_FromStopped_PutsDesiredBackToStopped()
    {
        Vm vm = RunningVm();
        vm.MarkObserved(Stopped);
        OperationTicket start = vm.Start();

        vm.EndOperation(start.OperationId, Failed("The VM could not be started."));

        Assert.Equal(VmDesiredState.Stopped, vm.DesiredState);
        Assert.Null(vm.Pending);
    }

    [Fact]
    public void Failed_OfADelete_PutsDesiredExistenceBackToExists_AndKeepsTheInstance()
    {
        Vm vm = RunningVm();
        OperationTicket delete = vm.Delete()!;

        VmOperationEnded? ended = vm.EndOperation(delete.OperationId, Failed("The VM could not be deleted."));

        Assert.Equal(VmOperationKind.Delete, ended!.Operation.Kind);
        Assert.Equal(DesiredExistence.Exists, vm.DesiredExistence);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
        Assert.Equal("42", vm.ExternalId);
        Assert.False(vm.IsTerminal);
        Assert.Null(vm.Pending);
    }

    [Fact]
    public void AfterAFailedStop_AnotherStop_IsANewRequest()
    {
        Vm vm = RunningVm();
        OperationTicket first = vm.Stop();
        vm.EndOperation(first.OperationId, Failed());

        OperationTicket second = vm.Stop();

        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(second.OperationId, vm.Pending!.Id);
    }

    // ---------- an observation never ends a request ----------

    [Fact]
    public void AnObservation_ReachingWhatWasAsked_LeavesTheRequestWaiting()
    {
        Vm vm = RunningVm();
        OperationTicket stop = vm.Stop();

        vm.MarkObserved(Stopped);

        Assert.Equal(stop.OperationId, vm.Pending!.Id);
        Assert.Equal(VmState.Stopped, vm.CurrentState);
        Assert.Equal(VmTransition.Stopping, vm.Transition);
    }

    [Fact]
    public void AnObservation_OfRunning_LeavesAWaitingCreateWaiting()
    {
        Vm vm = NewVm();
        Guid createId = vm.Pending!.Id;

        vm.MarkObserved(Running);

        Assert.Equal(createId, vm.Pending!.Id);
        Assert.Equal(VmState.Running, vm.CurrentState);
        Assert.Equal(CurrentExistence.Exists, vm.CurrentExistence);
    }

    [Fact]
    public void NotFound_LeavesAWaitingDeleteWaiting_AndClearsTheAddresses()
    {
        Vm vm = RunningVm();
        OperationTicket delete = vm.Delete()!;

        vm.MarkNotFound();

        Assert.Equal(delete.OperationId, vm.Pending!.Id);
        Assert.Equal(CurrentExistence.NotFound, vm.CurrentExistence);
        Assert.Null(vm.CurrentState);
        Assert.Null(vm.IpAddress);
    }

    [Fact] // a guest-OS shutdown nobody asked for: drift, not a request
    public void GuestShutdown_IsDrift_WithNoTransition()
    {
        Vm vm = RunningVm();

        vm.MarkObserved(Stopped);

        Assert.Null(vm.Pending);
        Assert.Null(vm.Transition);
        Assert.Equal(VmDesiredState.Running, vm.DesiredState);
        Assert.Equal(VmState.Stopped, vm.CurrentState);
    }

    // ---------- a delete of a VM the provider never had ----------

    [Fact]
    public void RequestDelete_WithoutAProviderId_AfterTheCreateFailed_HandsOutNoTicket()
    {
        Vm vm = NewVm();
        vm.EndOperation(vm.Pending!.Id, Failed("VM provisioning failed."));

        OperationTicket? ticket = vm.Delete();

        Assert.Null(ticket);
        Assert.Null(vm.Pending);
    }

    [Fact]
    public void RequestDelete_ReplacesAWaitingStop_WithANewId()
    {
        Vm vm = RunningVm();
        OperationTicket stop = vm.Stop();

        OperationTicket delete = vm.Delete()!;

        Assert.NotEqual(stop.OperationId, delete.OperationId);
        Assert.Equal(VmOperationKind.Delete, vm.Pending!.Kind);
        Assert.Equal("42", delete.ProviderId);
        // the stop's own report arrives later and is ignored
        Assert.Null(vm.EndOperation(stop.OperationId, VmOperationEnd.Done(null, Stopped)));
        Assert.Equal(delete.OperationId, vm.Pending!.Id);
    }

    // ---------- the value objects ----------

    [Fact]
    public void RequestTicket_RefusesAnEmptyProviderId()
    {
        Assert.Throws<Titan.Domain.Exceptions.BusinessException>(() => OperationTicket.Of(Guid.NewGuid(), ""));
    }

    [Fact]
    public void PendingRequest_RefusesAnEmptyId()
    {
        Assert.Throws<Titan.Domain.Exceptions.BusinessException>(
            () => PendingOperation<VmOperationKind>.Of(Guid.Empty, VmOperationKind.Stop, DateTimeOffset.UtcNow));
    }
}

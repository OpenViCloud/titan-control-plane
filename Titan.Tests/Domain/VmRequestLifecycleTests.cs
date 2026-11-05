using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants: a VM holds at most one waiting request (its id; the transition
// status says what it asks for); only the report that carries that id ends it; an observation never does; a failed
// request puts the status back to the last Incus word.
// VmStateMachineTests keeps what a request may do from each Incus status; this file is about how a request ends.
public class VmRequestLifecycleTests
{

    private static Vm NewVm() => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

    private static Vm RunningVm()
    {
        Vm vm = NewVm();
        vm.CreateSucceeded(vm.PendingOperationId!.Value, "42", VmStatus.Running, "10.0.0.5", null);
        return vm;
    }

    // The give-up for whatever the VM waits on, picked by its status as VmReports.ApplyFailedAsync does.
    private static bool Failed(Vm vm, Guid operationId,
        string reason = "The VM could not be stopped.", string detail = "raw")
    {
        switch (vm.Status)
        {
            case VmStatus.Creating:
                return vm.CreateFailed(operationId, null, reason, detail);
            case VmStatus.Starting:
                return vm.StartFailed(operationId, reason, detail);
            case VmStatus.Stopping:
                return vm.StopFailed(operationId, reason, detail);
            case VmStatus.Deleting:
                return vm.DeleteFailed(operationId, reason, detail);
            default:
                return false;
        }
    }

    // ---------- the create ----------

    [Fact]
    public void ANewVm_WaitsOnItsCreate_WithAnIdAndATime_AndIsCreating()
    {
        Vm vm = NewVm();

        Assert.NotNull(vm.PendingOperationId);
        Assert.NotEqual(Guid.Empty, vm.PendingOperationId!.Value);
        Assert.NotNull(vm.PendingOperationAskedAt);
        Assert.True(vm.PendingOperationAskedAt!.Value <= DateTimeOffset.UtcNow);
        Assert.Equal(VmStatus.Creating, vm.Status);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdAndStatus_AndEndsIt()
    {
        Vm vm = NewVm();
        Guid createId = vm.PendingOperationId!.Value;

        bool ended = vm.CreateSucceeded(createId, "42", VmStatus.Running, "10.0.0.5", null);

        Assert.True(ended);
        Assert.Null(vm.PendingOperationId);
        Assert.Equal("42", vm.ExternalId);
        Assert.True(vm.HasAnInstance);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal("10.0.0.5", vm.IpAddress);
        Assert.Null(vm.OperationFailureReason);
    }

    [Fact] // the instance was made but never answered: it exists, its create failed, and no Incus word is known yet
    public void Failed_OfTheCreate_WithAnId_KeepsTheIdAndTheInstance_RecordsTheReason_AndIsUnknown()
    {
        Vm vm = NewVm();

        bool ended = vm.CreateFailed(vm.PendingOperationId!.Value, "45", "VM provisioning failed.", "raw");

        Assert.True(ended);
        Assert.Null(vm.PendingOperationId);
        Assert.Equal("45", vm.ExternalId);
        Assert.True(vm.HasAnInstance);
        Assert.Null(vm.ObservedState);
        Assert.Equal(VmStatus.Unknown, vm.Status);
        Assert.True(vm.OccupiesQuota);
        Assert.Equal("VM provisioning failed.", vm.OperationFailureReason);
        Assert.Equal("raw", vm.OperationFailureDetail);
        Assert.NotNull(vm.OperationFailedAt);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_IsFailed()
    {
        Vm vm = NewVm();

        Failed(vm, vm.PendingOperationId!.Value, "VM provisioning failed.");

        Assert.Null(vm.PendingOperationId);
        Assert.Null(vm.ExternalId);
        Assert.Equal(VmStatus.Failed, vm.Status);
        Assert.False(vm.HasAnInstance);
    }

    // ---------- only the report with the right id ends a request ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsNull()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);
        Guid before = vm.PendingOperationId!.Value;

        bool ended = vm.StopSucceeded(Guid.NewGuid(), VmStatus.Stopped, null, null);

        Assert.False(ended);
        Assert.Equal(before, vm.PendingOperationId);
        Assert.Equal(stop, vm.PendingOperationId);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(VmStatus.Stopping, vm.Status);
    }

    [Fact]
    public void AReport_WhenNothingWaits_ChangesNothing()
    {
        Vm vm = RunningVm();
        Guid oldId = Guid.NewGuid();

        Assert.False(vm.StopSucceeded(oldId, VmStatus.Stopped, null, null));
        Assert.False(Failed(vm, oldId));

        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Null(vm.OperationFailureReason);
    }

    [Fact]
    public void TheSameDone_Twice_EndsTheRequestOnce()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        Assert.True(vm.StopSucceeded(stop, VmStatus.Stopped, null, null));
        Assert.False(vm.StopSucceeded(stop, VmStatus.Stopped, null, null));
    }

    [Fact] // a give-up re-sent after the customer pressed a new request must not free the new one
    public void AStaleGiveUp_AfterANewRequest_IsIgnored()
    {
        Vm vm = RunningVm();
        Guid firstStop = Guid.NewGuid();
        vm.Stop(firstStop);
        Failed(vm, firstStop);
        Guid secondStop = Guid.NewGuid();
        vm.Stop(secondStop);

        Assert.False(Failed(vm, firstStop));

        Assert.Equal(secondStop, vm.PendingOperationId);
        Assert.Equal(VmStatus.Stopping, vm.Status);
    }

    [Fact]
    public void ADeletedVm_AbsorbsEveryReportAndObservation()
    {
        Vm vm = RunningVm();
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);
        vm.DeleteSucceeded(delete);
        Assert.True(vm.IsDeleted);

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);
        Assert.False(vm.DeleteSucceeded(delete));
        Assert.False(Failed(vm, Guid.NewGuid()));

        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.Null(vm.ObservedState);
        Assert.Null(vm.PendingOperationId);
    }

    // ---------- Done by kind ----------

    [Fact]
    public void Done_OfAStop_RecordsWhatTheWorkerSaw_AndEndsIt()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        bool ended = vm.StopSucceeded(stop, VmStatus.Stopped, null, null);

        Assert.True(ended);
        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Stopped, vm.ObservedState);
        Assert.Equal(VmStatus.Stopped, vm.Status);
    }

    [Fact]
    public void Done_OfADelete_EndsTheVm_AndTakesItsSnapshots()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = vm.WithSnapshot("resting");
        vm.MarkSnapshotCreated(resting.Id, "s-1");
        VmSnapshot deleting = vm.WithSnapshot("deleting");
        vm.MarkSnapshotCreated(deleting.Id, "s-2");
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        bool ended = vm.DeleteSucceeded(delete);

        Assert.True(ended);
        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.Null(vm.ObservedState);
        Assert.Null(vm.IpAddress);
        Assert.True(vm.IsDeleted);
        Assert.All(vm.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
    }

    [Fact]
    public void Done_ClearsAnOlderFailureReason()
    {
        Vm vm = RunningVm();
        Guid firstStop = Guid.NewGuid();
        vm.Stop(firstStop);
        Failed(vm, firstStop);
        Assert.NotNull(vm.OperationFailureReason);
        Guid secondStop = Guid.NewGuid();
        vm.Stop(secondStop);

        vm.StopSucceeded(secondStop, VmStatus.Stopped, null, null);

        Assert.Null(vm.OperationFailureReason);
        Assert.Null(vm.OperationFailureDetail);
        Assert.Null(vm.OperationFailedAt);
    }

    // ---------- Failed by kind: the status goes back to the last Incus word ----------

    [Fact]
    public void Failed_OfAStop_PutsTheStatusBackToRunning_AndRecordsTheReason()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        bool ended = Failed(vm, stop, "The VM could not be stopped.");

        Assert.True(ended);
        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal("The VM could not be stopped.", vm.OperationFailureReason);
    }

    [Fact]
    public void Failed_OfAStart_FromStopped_PutsTheStatusBackToStopped()
    {
        Vm vm = RunningVm();
        vm.MarkObserved(VmStatus.Stopped, null, null);
        Guid start = Guid.NewGuid();
        vm.Start(start);

        Failed(vm, start, "The VM could not be started.");

        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Null(vm.PendingOperationId);
    }

    [Fact]
    public void Failed_OfADelete_PutsTheStatusBackToTheIncusWord_AndKeepsTheInstance()
    {
        Vm vm = RunningVm();
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        bool ended = Failed(vm, delete, "The VM could not be deleted.");

        Assert.True(ended);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.True(vm.HasAnInstance);
        Assert.Equal("42", vm.ExternalId);
        Assert.False(vm.IsDeleted);
        Assert.Null(vm.PendingOperationId);
    }

    [Fact]
    public void AfterAFailedStop_AnotherStop_IsANewRequest()
    {
        Vm vm = RunningVm();
        Guid first = Guid.NewGuid();
        vm.Stop(first);
        Failed(vm, first);

        Guid second = Guid.NewGuid();
        vm.Stop(second);

        Assert.Equal(second, vm.PendingOperationId);
        Assert.Equal(VmStatus.Stopping, vm.Status);
    }

    // ---------- an observation never ends a request ----------

    [Fact]
    public void AnObservation_ReachingWhatWasAsked_LeavesTheRequestWaiting()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Equal(stop, vm.PendingOperationId);
        Assert.Equal(VmStatus.Stopped, vm.ObservedState);
        Assert.Equal(VmStatus.Stopping, vm.Status);
    }

    [Fact]
    public void AnObservation_OfRunning_LeavesAWaitingCreateWaiting()
    {
        Vm vm = NewVm();
        Guid createId = vm.PendingOperationId!.Value;

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);

        Assert.Equal(createId, vm.PendingOperationId);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(VmStatus.Creating, vm.Status);
    }

    [Fact]
    public void NotFound_LeavesAWaitingDeleteWaiting_AndClearsTheAddresses()
    {
        Vm vm = RunningVm();
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        vm.MarkNotFound();

        Assert.Equal(delete, vm.PendingOperationId);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Null(vm.ObservedState);
        Assert.Null(vm.IpAddress);
    }

    [Fact] // a guest-OS shutdown nobody asked for: no request waits, so the status follows Incus
    public void GuestShutdown_WithNoRequestWaiting_IsStopped()
    {
        Vm vm = RunningVm();

        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.ObservedState);
    }

    // ---------- a delete of a VM the provider never had ----------

    [Fact]
    public void Delete_WithoutAProviderId_AfterTheCreateFailed_IsDeletedAtOnce_WithNothingWaiting()
    {
        Vm vm = NewVm();
        Failed(vm, vm.PendingOperationId!.Value, "VM provisioning failed.");

        vm.Delete(Guid.NewGuid());

        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.True(vm.IsDeleted);
        Assert.Null(vm.PendingOperationId);
    }

    [Fact]
    public void RequestDelete_ReplacesAWaitingStop_WithANewId()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        Assert.Equal(delete, vm.PendingOperationId);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Equal("42", vm.ExternalId);
        // the stop's own report arrives later and is ignored
        Assert.False(vm.StopSucceeded(stop, VmStatus.Stopped, null, null));
        Assert.Equal(delete, vm.PendingOperationId);
    }

    // ---------- the value objects ----------

    [Fact]
    public void RequestTicket_RefusesAnEmptyProviderId()
    {
        Assert.Throws<Titan.Domain.Exceptions.BusinessException>(() => OperationTicket.Of(Guid.NewGuid(), ""));
    }

    [Fact]
    public void AnOperation_RefusesAnEmptyId()
    {
        Vm vm = RunningVm();

        Assert.Throws<Titan.Domain.Exceptions.BusinessException>(() => vm.Stop(Guid.Empty));
        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Running, vm.Status);
    }

    [Fact]
    public void PendingRequest_RefusesAnEmptyId()
    {
        Assert.Throws<Titan.Domain.Exceptions.BusinessException>(
            () => PendingOperation<VmSnapshotOperationKind>.Of(Guid.Empty, VmSnapshotOperationKind.Revert, DateTimeOffset.UtcNow));
    }
}

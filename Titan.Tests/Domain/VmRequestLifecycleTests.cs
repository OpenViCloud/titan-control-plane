using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants: a VM holds at most one waiting request (its id; the transition
// status says what it asks for); only the report that carries that id ends it; an observation never does; a failed
// request puts the status back to the last Incus status.
// VmStateMachineTests keeps what a request may do from each Incus status; this file is about how a request ends.
public class VmRequestLifecycleTests
{

    private static Vm NewVm() => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

    private static Vm RunningVm()
    {
        Vm vm = NewVm();
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "42", VmStatus.Running, "10.0.0.5", null);
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

        Assert.NotNull(vm.Operation.PendingId);
        Assert.NotEqual(Guid.Empty, vm.Operation.PendingId!.Value);
        Assert.NotNull(vm.Operation.AskedAt);
        Assert.True(vm.Operation.AskedAt!.Value <= DateTimeOffset.UtcNow);
        Assert.Equal(VmStatus.Creating, vm.Status);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdAndStatus_AndEndsIt()
    {
        Vm vm = NewVm();
        Guid createId = vm.Operation.PendingId!.Value;

        bool ended = vm.CreateSucceeded(createId, "42", VmStatus.Running, "10.0.0.5", null);

        Assert.True(ended);
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal("42", vm.ExternalId);
        Assert.True(vm.HasAnInstance);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal("10.0.0.5", vm.IpAddress);
        Assert.Null(vm.Operation.FailureReason);
    }

    [Fact] // the instance was made but never answered: it exists, its create failed, and no Incus status is known yet
    public void Failed_OfTheCreate_WithAnId_KeepsTheIdAndTheInstance_RecordsTheReason_AndIsUnknown()
    {
        Vm vm = NewVm();

        bool ended = vm.CreateFailed(vm.Operation.PendingId!.Value, "45", "VM provisioning failed.", "raw");

        Assert.True(ended);
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal("45", vm.ExternalId);
        Assert.True(vm.HasAnInstance);
        Assert.Null(vm.InstanceStatus);
        Assert.Equal(VmStatus.Unknown, vm.Status);
        Assert.True(vm.OccupiesQuota);
        Assert.Equal("VM provisioning failed.", vm.Operation.FailureReason);
        Assert.Equal("raw", vm.Operation.FailureDetail);
        Assert.NotNull(vm.Operation.FailedAt);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_IsFailed()
    {
        Vm vm = NewVm();

        Failed(vm, vm.Operation.PendingId!.Value, "VM provisioning failed.");

        Assert.Null(vm.Operation.PendingId);
        Assert.Null(vm.ExternalId);
        Assert.Equal(VmStatus.Failed, vm.Status);
        Assert.False(vm.HasAnInstance);
    }

    // ---------- only the report with the right id ends a request ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsFalse()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);
        Guid before = vm.Operation.PendingId!.Value;

        bool ended = vm.StopSucceeded(Guid.NewGuid(), VmStatus.Stopped, null, null);

        Assert.False(ended);
        Assert.Equal(before, vm.Operation.PendingId);
        Assert.Equal(stop, vm.Operation.PendingId);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal(VmStatus.Stopping, vm.Status);
    }

    [Fact]
    public void AReport_WhenNothingWaits_ChangesNothing()
    {
        Vm vm = RunningVm();
        Guid oldId = Guid.NewGuid();

        Assert.False(vm.StopSucceeded(oldId, VmStatus.Stopped, null, null));
        Assert.False(Failed(vm, oldId));

        Assert.Null(vm.Operation.PendingId);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Null(vm.Operation.FailureReason);
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

        Assert.Equal(secondStop, vm.Operation.PendingId);
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
        Assert.Null(vm.InstanceStatus);
        Assert.Null(vm.Operation.PendingId);
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
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
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
        Assert.Null(vm.InstanceStatus);
        Assert.Null(vm.IpAddress);
        Assert.True(vm.IsDeleted);
        Assert.All(vm.Snapshots, s => Assert.Equal(VmSnapshotStatus.Deleted, s.Status));
    }

    [Fact]
    public void Done_ClearsAnOlderFailureReason()
    {
        Vm vm = RunningVm();
        Guid firstStop = Guid.NewGuid();
        vm.Stop(firstStop);
        Failed(vm, firstStop);
        Assert.NotNull(vm.Operation.FailureReason);
        Guid secondStop = Guid.NewGuid();
        vm.Stop(secondStop);

        vm.StopSucceeded(secondStop, VmStatus.Stopped, null, null);

        Assert.Null(vm.Operation.FailureReason);
        Assert.Null(vm.Operation.FailureDetail);
        Assert.Null(vm.Operation.FailedAt);
    }

    // ---------- Failed by kind: the status goes back to the last Incus status ----------

    [Fact]
    public void Failed_OfAStop_PutsTheStatusBackToRunning_AndRecordsTheReason()
    {
        Vm vm = RunningVm();
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        bool ended = Failed(vm, stop, "The VM could not be stopped.");

        Assert.True(ended);
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal("The VM could not be stopped.", vm.Operation.FailureReason);
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
        Assert.Null(vm.Operation.PendingId);
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
        Assert.Null(vm.Operation.PendingId);
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

        Assert.Equal(second, vm.Operation.PendingId);
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

        Assert.Equal(stop, vm.Operation.PendingId);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
        Assert.Equal(VmStatus.Stopping, vm.Status);
    }

    [Fact]
    public void AnObservation_OfRunning_LeavesAWaitingCreateWaiting()
    {
        Vm vm = NewVm();
        Guid createId = vm.Operation.PendingId!.Value;

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);

        Assert.Equal(createId, vm.Operation.PendingId);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Equal(VmStatus.Creating, vm.Status);
    }

    [Fact]
    public void NotFound_LeavesAWaitingDeleteWaiting_AndClearsTheAddresses()
    {
        Vm vm = RunningVm();
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        vm.MarkNotFound();

        Assert.Equal(delete, vm.Operation.PendingId);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Null(vm.InstanceStatus);
        Assert.Null(vm.IpAddress);
    }

    [Fact] // a guest-OS shutdown nobody asked for: no request waits, so the status follows Incus
    public void GuestShutdown_WithNoRequestWaiting_IsStopped()
    {
        Vm vm = RunningVm();

        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Null(vm.Operation.PendingId);
        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
    }

    // ---------- a delete of a VM the provider never had ----------

    [Fact]
    public void Delete_WithoutAProviderId_AfterTheCreateFailed_IsDeletedAtOnce_WithNothingWaiting()
    {
        Vm vm = NewVm();
        Failed(vm, vm.Operation.PendingId!.Value, "VM provisioning failed.");

        vm.Delete(Guid.NewGuid());

        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.True(vm.IsDeleted);
        Assert.Null(vm.Operation.PendingId);
    }

    // ---------- the value objects ----------

    [Fact]
    public void RequestTicket_RefusesAnEmptyProviderId()
    {
        Assert.Throws<Titan.Domain.Exceptions.BusinessException>(() => OperationTicket.Of(Guid.NewGuid(), ""));
    }

    // Each intent checks its own id, first: a missing snapshot would answer 404 if the id came second.
    public static TheoryData<string> OperationsThatTakeAnId() =>
        ["Stop", "Start", "Delete", "AddSnapshot", "RevertSnapshot", "DeleteSnapshot"];

    [Theory]
    [MemberData(nameof(OperationsThatTakeAnId))]
    public void AnOperation_RefusesAnEmptyId(string operation)
    {
        Vm vm = RunningVm();
        Guid snapshotId = Guid.NewGuid();

        Action ask = operation switch
        {
            "Stop" => () => vm.Stop(Guid.Empty),
            "Start" => () => vm.Start(Guid.Empty),
            "Delete" => () => vm.Delete(Guid.Empty),
            "AddSnapshot" => () => vm.AddSnapshot(snapshotId, Guid.Empty, "snap"),
            "RevertSnapshot" => () => vm.RevertSnapshot(snapshotId, Guid.Empty),
            _ => () => vm.DeleteSnapshot(snapshotId, Guid.Empty),
        };

        var refused = Assert.Throws<Titan.Domain.Exceptions.BusinessException>(ask);
        Assert.Equal("INVALID_REQUEST", refused.Code); // not the status refusal or the snapshot's own check
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Empty(vm.Snapshots);
    }
}

using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a VM snapshot: one waiting request (its own, independent of
// the VM's), ended only by the report carrying its id; an observation writes current and ends nothing; each kind's Done
// and Failed have their effect (titan-ops architecture/resource-lifecycle.md#kinds). VmSnapshotStateMachineTests keeps what
// a request may do from each situation; this file is about how a request ends, through the VM that is its root.
public class VmSnapshotRequestLifecycleTests
{
    private static Vm RunningVm()
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.EndOperation(vm.Pending!.Id, VmOperationEnd.Done("vm42", new VmObservation(VmState.Running, "10.0.0.5", null)));
        return vm;
    }

    private static VmSnapshot Creating(Vm vm) => vm.AddSnapshot("snap-1").Snapshot;

    private static VmSnapshot Ready(Vm vm)
    {
        VmSnapshot snapshot = Creating(vm);
        vm.EndSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, VmSnapshotOperationEnd.Done("vm42/s1"));
        return snapshot;
    }

    private static VmSnapshotOperationEnd Failed(string? externalId = null) =>
        VmSnapshotOperationEnd.Failed(externalId, "The snapshot request could not be completed.", "raw");

    private static string Shape(VmSnapshot s) =>
        $"{s.CurrentExistence}/{s.DesiredExistence}/{(s.Transition == null ? "-" : s.Transition.Value.ToString())}";

    // ---------- the create ----------

    [Fact]
    public void ANewSnapshot_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        VmSnapshot snapshot = Creating(RunningVm());

        Assert.Equal(VmSnapshotOperationKind.Create, snapshot.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, snapshot.Pending.Id);
        Assert.True(snapshot.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(VmSnapshotTransition.Creating, snapshot.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdAndExistence_AndEndsIt_Once()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);
        Guid createId = snapshot.Pending!.Id;

        PendingOperation<VmSnapshotOperationKind>? ended = vm.EndSnapshotOperation(snapshot.Id, createId, VmSnapshotOperationEnd.Done("vm42/s1"));
        PendingOperation<VmSnapshotOperationKind>? again = vm.EndSnapshotOperation(snapshot.Id, createId, VmSnapshotOperationEnd.Done("vm42/s9"));

        Assert.Equal((VmSnapshotOperationKind.Create, createId), (ended!.Kind, ended.Id));
        Assert.Null(again);
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Null(snapshot.OperationFailureReason);
    }

    [Fact]
    public void Failed_OfTheCreate_RecordsWhy_AndLeavesNothingOnIncus()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        vm.EndSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, Failed());

        Assert.Equal("NotCreated/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.OperationFailureReason);
        Assert.Equal("raw", snapshot.OperationFailureDetail);
        Assert.NotNull(snapshot.OperationFailedAt);
        Assert.False(snapshot.CountsTowardQuota);
        Assert.Equal(0, vm.SnapshotQuotaUsage);
    }

    // The give-up found the snapshot on Incus: it is kept with its id, so it can be deleted through Incus.
    [Fact]
    public void Failed_OfTheCreate_CarryingAnId_KeepsTheIdAndExistence()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        vm.EndSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, Failed("vm42/s1"));

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.NotNull(snapshot.OperationFailureReason);
        Assert.Equal("vm42/s1", vm.DeleteSnapshot(snapshot.Id)!.ProviderId);
    }

    // ---------- the revert ----------

    [Fact]
    public void Done_OfTheRevert_EndsIt_AndChangesNothingElse()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        OperationTicket revert = vm.RevertSnapshot(snapshot.Id);
        Assert.Equal(VmSnapshotOperationKind.Revert, snapshot.Pending!.Kind);
        Assert.Equal(revert.OperationId, snapshot.Pending.Id);

        PendingOperation<VmSnapshotOperationKind>? ended = vm.EndSnapshotOperation(snapshot.Id, revert.OperationId, VmSnapshotOperationEnd.Done(null));

        Assert.Equal(VmSnapshotOperationKind.Revert, ended!.Kind);
        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
    }

    [Fact]
    public void Failed_OfTheRevert_RecordsWhy_AndKeepsTheSnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        OperationTicket revert = vm.RevertSnapshot(snapshot.Id);

        vm.EndSnapshotOperation(snapshot.Id, revert.OperationId, Failed());

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.OperationFailureReason);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
    }

    // A Done clears the reason an earlier request left (titan-ops architecture/desired-and-current.md#request-failure).
    [Fact]
    public void Done_OfALaterRequest_ClearsTheOldReason()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        vm.EndSnapshotOperation(snapshot.Id, vm.RevertSnapshot(snapshot.Id).OperationId, Failed());

        vm.EndSnapshotOperation(snapshot.Id, vm.RevertSnapshot(snapshot.Id).OperationId, VmSnapshotOperationEnd.Done(null));

        Assert.Null(snapshot.OperationFailureReason);
        Assert.Null(snapshot.OperationFailureDetail);
        Assert.Null(snapshot.OperationFailedAt);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheSnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        OperationTicket delete = vm.DeleteSnapshot(snapshot.Id)!;

        PendingOperation<VmSnapshotOperationKind>? ended = vm.EndSnapshotOperation(snapshot.Id, delete.OperationId, VmSnapshotOperationEnd.Done(null));

        Assert.Equal(VmSnapshotOperationKind.Delete, ended!.Kind);
        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
        Assert.True(snapshot.IsTerminal);
        Assert.Null(vm.EndSnapshotOperation(snapshot.Id, delete.OperationId, VmSnapshotOperationEnd.Done(null)));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheSnapshotBack_WithWhy_AndTheNextDeleteIsANewRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        OperationTicket first = vm.DeleteSnapshot(snapshot.Id)!;
        Assert.Equal(0, vm.SnapshotQuotaUsage);

        vm.EndSnapshotOperation(snapshot.Id, first.OperationId, Failed());

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.OperationFailureReason);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
        OperationTicket second = vm.DeleteSnapshot(snapshot.Id)!;
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    // ---------- only the matching id ends a request ----------

    [Fact]
    public void AReportNamingAnotherRequest_EndsNothing_AndChangesNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        vm.RevertSnapshot(reverting.Id);

        Assert.Null(vm.EndSnapshotOperation(creating.Id, Guid.NewGuid(), VmSnapshotOperationEnd.Done("vm42/s9")));
        Assert.Null(vm.EndSnapshotOperation(reverting.Id, Guid.NewGuid(), Failed()));
        // The other snapshot's request id is not this one's.
        Assert.Null(vm.EndSnapshotOperation(creating.Id, reverting.Pending!.Id, VmSnapshotOperationEnd.Done("vm42/s9")));

        Assert.Equal("NotCreated/Exists/Creating", Shape(creating));
        Assert.Null(creating.ExternalId);
        Assert.Equal("Exists/Exists/Reverting", Shape(reverting));
        Assert.Null(reverting.OperationFailureReason);
    }

    // A snapshot's request is its own: the VM's report ends the VM's request and leaves the snapshot's waiting.
    [Fact]
    public void TheVmsReport_DoesNotEndTheSnapshotsRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        OperationTicket revert = vm.RevertSnapshot(snapshot.Id);

        Assert.Null(vm.EndOperation(revert.OperationId, VmOperationEnd.Done(null, null)));

        Assert.Equal(VmSnapshotTransition.Reverting, snapshot.Transition);
    }

    // ---------- observations end nothing ----------

    [Fact]
    public void Observations_WriteExistence_AndEndNoRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        vm.RevertSnapshot(reverting.Id);
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id);

        vm.MarkSnapshotObserved(creating.Id);
        vm.MarkSnapshotObserved(reverting.Id);
        vm.MarkSnapshotNotFound(deleting.Id);

        Assert.Equal("Exists/Exists/Creating", Shape(creating));
        Assert.Equal("Exists/Exists/Reverting", Shape(reverting));
        Assert.Equal("NotFound/Deleted/Deleting", Shape(deleting));

        vm.MarkSnapshotNotFound(creating.Id);
        Assert.Equal("NotFound/Exists/Creating", Shape(creating));
    }

    [Fact]
    public void ADeletedSnapshot_AbsorbsEveryReportAndObservation()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        OperationTicket delete = vm.DeleteSnapshot(snapshot.Id)!;
        vm.EndSnapshotOperation(snapshot.Id, delete.OperationId, VmSnapshotOperationEnd.Done(null));

        vm.MarkSnapshotObserved(snapshot.Id);
        vm.MarkSnapshotNotFound(snapshot.Id);
        Assert.Null(vm.EndSnapshotOperation(snapshot.Id, delete.OperationId, Failed()));

        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => vm.DeleteSnapshot(snapshot.Id)).Code);
    }

    // ---------- the VM's delete ----------

    // A create or revert in flight touches the instance; a delete waiting does not (titan-ops features/vm/design.md#vm-snapshot-counting).
    [Fact]
    public void BlocksVmDelete_WhileACreateOrARevertWaits_NotWhileADeleteWaits_NorOnceTheyEnded()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        OperationTicket revert = vm.RevertSnapshot(reverting.Id);
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id);

        Assert.True(creating.BlocksVmDelete);
        Assert.True(reverting.BlocksVmDelete);
        Assert.False(deleting.BlocksVmDelete);
        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", Assert.Throws<BusinessException>(() => vm.Delete()).Code);

        vm.EndSnapshotOperation(creating.Id, creating.Pending!.Id, Failed());
        vm.EndSnapshotOperation(reverting.Id, revert.OperationId, Failed());

        Assert.False(creating.BlocksVmDelete);
        Assert.False(reverting.BlocksVmDelete);
        Assert.NotNull(vm.Delete());
    }

    // Incus deletes an instance's snapshots with it: the VM's delete Done ends every snapshot, whatever it waited on,
    // and names only those whose own delete was waiting, for their outcome.
    [Fact]
    public void TheVmsDeleteDone_EndsEverySnapshot_AndNamesOnlyThoseWhoseOwnDeleteWaited()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Ready(vm);
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id);
        VmSnapshot failedCreate = Creating(vm);
        vm.EndSnapshotOperation(failedCreate.Id, failedCreate.Pending!.Id, Failed());
        OperationTicket vmDelete = vm.Delete()!;

        VmOperationEnded? ended = vm.EndOperation(vmDelete.OperationId, VmOperationEnd.Done(null, null));

        Assert.Equal([deleting.Id], ended!.SnapshotsWhoseDeleteEnded.Select(s => s.Id));
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Null(s.Pending));
    }

    [Fact]
    public void TheVmsDeleteFailed_LeavesTheSnapshotsAsTheyWere()
    {
        Vm vm = RunningVm();
        VmSnapshot deleting = Ready(vm);
        OperationTicket snapshotDelete = vm.DeleteSnapshot(deleting.Id)!;
        OperationTicket vmDelete = vm.Delete()!;

        VmOperationEnded? ended = vm.EndOperation(vmDelete.OperationId, VmOperationEnd.Failed(null, null, "refused", "raw"));

        Assert.Empty(ended!.SnapshotsWhoseDeleteEnded);
        Assert.Equal("Exists/Deleted/Deleting", Shape(deleting));
        Assert.Equal(snapshotDelete.OperationId, deleting.Pending!.Id);
    }

    [Fact]
    public void EndWithItsVm_SaysWhetherItsOwnDeleteWaited_Once()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Ready(vm);
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id);

        Assert.False(resting.EndWithItsVm());
        Assert.True(deleting.EndWithItsVm());
        Assert.False(deleting.EndWithItsVm());
        Assert.True(resting.IsTerminal);
        Assert.Null(deleting.Transition);
    }
}

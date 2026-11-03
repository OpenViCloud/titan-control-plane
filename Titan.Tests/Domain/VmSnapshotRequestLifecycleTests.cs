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
        vm.EndRequest(vm.Pending!.Id, VmRequestEnd.Done("vm42", new VmObservation(VmState.Running, "10.0.0.5", null)));
        return vm;
    }

    private static VmSnapshot Creating(Vm vm) => vm.AddSnapshot("snap-1").Snapshot;

    private static VmSnapshot Ready(Vm vm)
    {
        VmSnapshot snapshot = Creating(vm);
        vm.EndSnapshotRequest(snapshot.Id, snapshot.Pending!.Id, VmSnapshotRequestEnd.Done("vm42/s1"));
        return snapshot;
    }

    private static VmSnapshotRequestEnd Failed(string? externalId = null) =>
        VmSnapshotRequestEnd.Failed(externalId, "The snapshot request could not be completed.", "raw");

    private static string Shape(VmSnapshot s) =>
        $"{s.CurrentExistence}/{s.DesiredExistence}/{(s.Transition == null ? "-" : s.Transition.Value.ToString())}";

    // ---------- the create ----------

    [Fact]
    public void ANewSnapshot_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        VmSnapshot snapshot = Creating(RunningVm());

        Assert.Equal(VmSnapshotRequestKind.Create, snapshot.Pending!.Kind);
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

        PendingRequest<VmSnapshotRequestKind>? ended = vm.EndSnapshotRequest(snapshot.Id, createId, VmSnapshotRequestEnd.Done("vm42/s1"));
        PendingRequest<VmSnapshotRequestKind>? again = vm.EndSnapshotRequest(snapshot.Id, createId, VmSnapshotRequestEnd.Done("vm42/s9"));

        Assert.Equal((VmSnapshotRequestKind.Create, createId), (ended!.Kind, ended.Id));
        Assert.Null(again);
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Null(snapshot.RequestFailureReason);
    }

    [Fact]
    public void Failed_OfTheCreate_RecordsWhy_AndLeavesNothingOnIncus()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        vm.EndSnapshotRequest(snapshot.Id, snapshot.Pending!.Id, Failed());

        Assert.Equal("NotCreated/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.RequestFailureReason);
        Assert.Equal("raw", snapshot.RequestFailureDetail);
        Assert.NotNull(snapshot.RequestFailedAt);
        Assert.False(snapshot.CountsTowardQuota);
        Assert.Equal(0, vm.SnapshotQuotaUsage);
    }

    // The give-up found the snapshot on Incus: it is kept with its id, so it can be deleted through Incus.
    [Fact]
    public void Failed_OfTheCreate_CarryingAnId_KeepsTheIdAndExistence()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        vm.EndSnapshotRequest(snapshot.Id, snapshot.Pending!.Id, Failed("vm42/s1"));

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.NotNull(snapshot.RequestFailureReason);
        Assert.Equal("vm42/s1", vm.RequestSnapshotDelete(snapshot.Id).ProviderId);
    }

    // ---------- the revert ----------

    [Fact]
    public void Done_OfTheRevert_EndsIt_AndChangesNothingElse()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        RequestTicket revert = vm.RevertSnapshot(snapshot.Id);
        Assert.Equal(VmSnapshotRequestKind.Revert, snapshot.Pending!.Kind);
        Assert.Equal(revert.RequestId, snapshot.Pending.Id);

        PendingRequest<VmSnapshotRequestKind>? ended = vm.EndSnapshotRequest(snapshot.Id, revert.RequestId, VmSnapshotRequestEnd.Done(null));

        Assert.Equal(VmSnapshotRequestKind.Revert, ended!.Kind);
        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
    }

    [Fact]
    public void Failed_OfTheRevert_RecordsWhy_AndKeepsTheSnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        RequestTicket revert = vm.RevertSnapshot(snapshot.Id);

        vm.EndSnapshotRequest(snapshot.Id, revert.RequestId, Failed());

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.RequestFailureReason);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
    }

    // A Done clears the reason an earlier request left (titan-ops architecture/desired-and-current.md#request-failure).
    [Fact]
    public void Done_OfALaterRequest_ClearsTheOldReason()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        vm.EndSnapshotRequest(snapshot.Id, vm.RevertSnapshot(snapshot.Id).RequestId, Failed());

        vm.EndSnapshotRequest(snapshot.Id, vm.RevertSnapshot(snapshot.Id).RequestId, VmSnapshotRequestEnd.Done(null));

        Assert.Null(snapshot.RequestFailureReason);
        Assert.Null(snapshot.RequestFailureDetail);
        Assert.Null(snapshot.RequestFailedAt);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheSnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        RequestTicket delete = vm.RequestSnapshotDelete(snapshot.Id);

        PendingRequest<VmSnapshotRequestKind>? ended = vm.EndSnapshotRequest(snapshot.Id, delete.RequestId, VmSnapshotRequestEnd.Done(null));

        Assert.Equal(VmSnapshotRequestKind.Delete, ended!.Kind);
        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
        Assert.True(snapshot.IsTerminal);
        Assert.Null(vm.EndSnapshotRequest(snapshot.Id, delete.RequestId, VmSnapshotRequestEnd.Done(null)));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheSnapshotBack_WithWhy_AndTheNextDeleteIsANewRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        RequestTicket first = vm.RequestSnapshotDelete(snapshot.Id);
        Assert.Equal(0, vm.SnapshotQuotaUsage);

        vm.EndSnapshotRequest(snapshot.Id, first.RequestId, Failed());

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.RequestFailureReason);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
        RequestTicket second = vm.RequestSnapshotDelete(snapshot.Id);
        Assert.NotEqual(first.RequestId, second.RequestId);
    }

    // ---------- only the matching id ends a request ----------

    [Fact]
    public void AReportNamingAnotherRequest_EndsNothing_AndChangesNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        vm.RevertSnapshot(reverting.Id);

        Assert.Null(vm.EndSnapshotRequest(creating.Id, Guid.NewGuid(), VmSnapshotRequestEnd.Done("vm42/s9")));
        Assert.Null(vm.EndSnapshotRequest(reverting.Id, Guid.NewGuid(), Failed()));
        // The other snapshot's request id is not this one's.
        Assert.Null(vm.EndSnapshotRequest(creating.Id, reverting.Pending!.Id, VmSnapshotRequestEnd.Done("vm42/s9")));

        Assert.Equal("NotCreated/Exists/Creating", Shape(creating));
        Assert.Null(creating.ExternalId);
        Assert.Equal("Exists/Exists/Reverting", Shape(reverting));
        Assert.Null(reverting.RequestFailureReason);
    }

    // A snapshot's request is its own: the VM's report ends the VM's request and leaves the snapshot's waiting.
    [Fact]
    public void TheVmsReport_DoesNotEndTheSnapshotsRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        RequestTicket revert = vm.RevertSnapshot(snapshot.Id);

        Assert.Null(vm.EndRequest(revert.RequestId, VmRequestEnd.Done(null, null)));

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
        vm.RequestSnapshotDelete(deleting.Id);

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
        RequestTicket delete = vm.RequestSnapshotDelete(snapshot.Id);
        vm.EndSnapshotRequest(snapshot.Id, delete.RequestId, VmSnapshotRequestEnd.Done(null));

        vm.MarkSnapshotObserved(snapshot.Id);
        vm.MarkSnapshotNotFound(snapshot.Id);
        Assert.Null(vm.EndSnapshotRequest(snapshot.Id, delete.RequestId, Failed()));

        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => vm.RequestSnapshotDelete(snapshot.Id)).Code);
    }

    // ---------- the VM's delete ----------

    // A create or revert in flight touches the instance; a delete waiting does not (titan-ops features/vm/design.md#vm-snapshot-counting).
    [Fact]
    public void BlocksVmDelete_WhileACreateOrARevertWaits_NotWhileADeleteWaits_NorOnceTheyEnded()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        RequestTicket revert = vm.RevertSnapshot(reverting.Id);
        VmSnapshot deleting = Ready(vm);
        vm.RequestSnapshotDelete(deleting.Id);

        Assert.True(creating.BlocksVmDelete);
        Assert.True(reverting.BlocksVmDelete);
        Assert.False(deleting.BlocksVmDelete);
        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", Assert.Throws<BusinessException>(() => vm.RequestDelete()).Code);

        vm.EndSnapshotRequest(creating.Id, creating.Pending!.Id, Failed());
        vm.EndSnapshotRequest(reverting.Id, revert.RequestId, Failed());

        Assert.False(creating.BlocksVmDelete);
        Assert.False(reverting.BlocksVmDelete);
        Assert.NotNull(vm.RequestDelete());
    }

    // Incus deletes an instance's snapshots with it: the VM's delete Done ends every snapshot, whatever it waited on,
    // and names only those whose own delete was waiting, for their outcome.
    [Fact]
    public void TheVmsDeleteDone_EndsEverySnapshot_AndNamesOnlyThoseWhoseOwnDeleteWaited()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Ready(vm);
        VmSnapshot deleting = Ready(vm);
        vm.RequestSnapshotDelete(deleting.Id);
        VmSnapshot failedCreate = Creating(vm);
        vm.EndSnapshotRequest(failedCreate.Id, failedCreate.Pending!.Id, Failed());
        RequestTicket vmDelete = vm.RequestDelete()!;

        VmRequestEnded? ended = vm.EndRequest(vmDelete.RequestId, VmRequestEnd.Done(null, null));

        Assert.Equal([deleting.Id], ended!.SnapshotsWhoseDeleteEnded.Select(s => s.Id));
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Null(s.Pending));
    }

    [Fact]
    public void TheVmsDeleteFailed_LeavesTheSnapshotsAsTheyWere()
    {
        Vm vm = RunningVm();
        VmSnapshot deleting = Ready(vm);
        RequestTicket snapshotDelete = vm.RequestSnapshotDelete(deleting.Id);
        RequestTicket vmDelete = vm.RequestDelete()!;

        VmRequestEnded? ended = vm.EndRequest(vmDelete.RequestId, VmRequestEnd.Failed(null, null, "refused", "raw"));

        Assert.Empty(ended!.SnapshotsWhoseDeleteEnded);
        Assert.Equal("Exists/Deleted/Deleting", Shape(deleting));
        Assert.Equal(snapshotDelete.RequestId, deleting.Pending!.Id);
    }

    [Fact]
    public void EndWithItsVm_SaysWhetherItsOwnDeleteWaited_Once()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Ready(vm);
        VmSnapshot deleting = Ready(vm);
        vm.RequestSnapshotDelete(deleting.Id);

        Assert.False(resting.EndWithItsVm());
        Assert.True(deleting.EndWithItsVm());
        Assert.False(deleting.EndWithItsVm());
        Assert.True(resting.IsTerminal);
        Assert.Null(deleting.Transition);
    }
}

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
        vm.CreateSucceeded(vm.PendingOperationId!.Value, "vm42", VmStatus.Running, "10.0.0.5", null);
        return vm;
    }

    private static VmSnapshot Creating(Vm vm) => vm.WithSnapshot("snap-1");

    private static VmSnapshot Ready(Vm vm)
    {
        VmSnapshot snapshot = Creating(vm);
        vm.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, "vm42/s1");
        return snapshot;
    }

    private static VmSnapshotOperationKind? Failed(Vm vm, Guid snapshotId, Guid operationId,
        string? externalId = null) =>
        vm.FailSnapshotOperation(snapshotId, operationId, externalId, "The snapshot request could not be completed.", "raw");

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

        VmSnapshotOperationKind? ended = vm.FinishSnapshotOperation(snapshot.Id, createId, "vm42/s1");
        VmSnapshotOperationKind? again = vm.FinishSnapshotOperation(snapshot.Id, createId, "vm42/s9");

        Assert.Equal(VmSnapshotOperationKind.Create, ended);
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

        Failed(vm, snapshot.Id, snapshot.Pending!.Id);

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

        Failed(vm, snapshot.Id, snapshot.Pending!.Id, "vm42/s1");

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.NotNull(snapshot.OperationFailureReason);
        vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid());
        Assert.Equal(VmSnapshotTransition.Deleting, snapshot.Transition);
        Assert.Equal("vm42/s1", snapshot.GetExternalId());
    }

    // ---------- the revert ----------

    [Fact]
    public void Done_OfTheRevert_EndsIt_AndChangesNothingElse()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);
        Assert.Equal(VmSnapshotOperationKind.Revert, snapshot.Pending!.Kind);
        Assert.Equal(revert, snapshot.Pending.Id);

        VmSnapshotOperationKind? ended = vm.FinishSnapshotOperation(snapshot.Id, revert, null);

        Assert.Equal(VmSnapshotOperationKind.Revert, ended);
        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
    }

    [Fact]
    public void Failed_OfTheRevert_RecordsWhy_AndKeepsTheSnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);

        Failed(vm, snapshot.Id, revert);

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
        Guid firstRevert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, firstRevert);
        Failed(vm, snapshot.Id, firstRevert);
        Guid secondRevert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, secondRevert);

        vm.FinishSnapshotOperation(snapshot.Id, secondRevert, null);

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
        Guid delete = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, delete);

        VmSnapshotOperationKind? ended = vm.FinishSnapshotOperation(snapshot.Id, delete, null);

        Assert.Equal(VmSnapshotOperationKind.Delete, ended);
        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
        Assert.True(snapshot.IsDeleted);
        Assert.Null(vm.FinishSnapshotOperation(snapshot.Id, delete, null));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheSnapshotBack_WithWhy_AndTheNextDeleteIsANewRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Ready(vm);
        Guid first = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, first);
        Assert.Equal(0, vm.SnapshotQuotaUsage);

        Failed(vm, snapshot.Id, first);

        Assert.Equal("Exists/Exists/-", Shape(snapshot));
        Assert.Equal("The snapshot request could not be completed.", snapshot.OperationFailureReason);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
        Guid second = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, second);
        Assert.Equal(second, snapshot.PendingOperationId);
        Assert.Equal(VmSnapshotTransition.Deleting, snapshot.Transition);
    }

    // ---------- only the matching id ends a request ----------

    [Fact]
    public void AReportNamingAnotherRequest_EndsNothing_AndChangesNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        vm.RevertSnapshot(reverting.Id, Guid.NewGuid());

        Assert.Null(vm.FinishSnapshotOperation(creating.Id, Guid.NewGuid(), "vm42/s9"));
        Assert.Null(Failed(vm, reverting.Id, Guid.NewGuid()));
        // The other snapshot's request id is not this one's.
        Assert.Null(vm.FinishSnapshotOperation(creating.Id, reverting.Pending!.Id, "vm42/s9"));

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
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);

        Assert.False(vm.StopSucceeded(revert, null, null, null));

        Assert.Equal(VmSnapshotTransition.Reverting, snapshot.Transition);
    }

    // ---------- observations end nothing ----------

    [Fact]
    public void Observations_WriteExistence_AndEndNoRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        vm.RevertSnapshot(reverting.Id, Guid.NewGuid());
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());

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
        Guid delete = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, delete);
        vm.FinishSnapshotOperation(snapshot.Id, delete, null);

        vm.MarkSnapshotObserved(snapshot.Id);
        vm.MarkSnapshotNotFound(snapshot.Id);
        Assert.Null(Failed(vm, snapshot.Id, delete));

        Assert.Equal("Deleted/Deleted/-", Shape(snapshot));
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid())).Code);
    }

    // ---------- the VM's delete ----------

    // A create or revert in flight touches the instance; a delete waiting does not (titan-ops features/vm/design.md#vm-snapshot-counting).
    [Fact]
    public void BlocksVmDelete_WhileACreateOrARevertWaits_NotWhileADeleteWaits_NorOnceTheyEnded()
    {
        Vm vm = RunningVm();
        VmSnapshot creating = Creating(vm);
        VmSnapshot reverting = Ready(vm);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(reverting.Id, revert);
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());

        Assert.True(creating.BlocksVmDelete);
        Assert.True(reverting.BlocksVmDelete);
        Assert.False(deleting.BlocksVmDelete);
        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);

        Failed(vm, creating.Id, creating.Pending!.Id);
        Failed(vm, reverting.Id, revert);

        Assert.False(creating.BlocksVmDelete);
        Assert.False(reverting.BlocksVmDelete);
        vm.Delete(Guid.NewGuid());
        Assert.Equal(VmStatus.Deleting, vm.Status);
    }

    // Incus deletes an instance's snapshots with it: the VM's delete Done ends every snapshot, whatever it waited on.
    [Fact]
    public void TheVmsDeleteDone_EndsEverySnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Ready(vm);
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());
        VmSnapshot failedCreate = Creating(vm);
        Failed(vm, failedCreate.Id, failedCreate.Pending!.Id);
        Guid vmDelete = Guid.NewGuid();
        vm.Delete(vmDelete);

        bool ended = vm.DeleteSucceeded(vmDelete);

        Assert.True(ended);
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Null(s.Pending));
    }

    [Fact]
    public void TheVmsDeleteFailed_LeavesTheSnapshotsAsTheyWere()
    {
        Vm vm = RunningVm();
        VmSnapshot deleting = Ready(vm);
        Guid snapshotDelete = Guid.NewGuid();
        vm.DeleteSnapshot(deleting.Id, snapshotDelete);
        Guid vmDelete = Guid.NewGuid();
        vm.Delete(vmDelete);

        bool ended = vm.DeleteFailed(vmDelete, "refused", "raw");

        Assert.True(ended);
        Assert.Equal("Exists/Deleted/Deleting", Shape(deleting));
        Assert.Equal(snapshotDelete, deleting.Pending!.Id);
    }

    [Fact]
    public void EndWithItsVm_EndsTheSnapshot_WhateverItWaitedOn_AndARepeatChangesNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Ready(vm);
        VmSnapshot deleting = Ready(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());

        resting.EndWithItsVm();
        deleting.EndWithItsVm();
        deleting.EndWithItsVm();

        Assert.True(resting.IsDeleted);
        Assert.True(deleting.IsDeleted);
        Assert.Null(deleting.Pending);
        Assert.Null(deleting.Transition);
    }
}

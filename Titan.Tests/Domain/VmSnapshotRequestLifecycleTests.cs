using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a VM snapshot: one waiting operation (its own, independent of
// the VM's), ended only by the report carrying its id to the method of the operation the status says is waiting; an
// observation records what is seen and ends nothing; each operation's Succeeded and Failed have their effect
// (titan-ops features/vm/design.md#vm-snapshot-reports). VmSnapshotStateMachineTests keeps what an operation may do from
// each status; this file is about how an operation ends, through the VM that is its root.
public class VmSnapshotRequestLifecycleTests
{
    private const string Reason = "The snapshot request could not be completed.";

    private static Vm RunningVm()
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm42", VmStatus.Running, "10.0.0.5", null);
        return vm;
    }

    private static VmSnapshot Creating(Vm vm) => vm.WithSnapshot("snap-1");

    private static VmSnapshot Available(Vm vm)
    {
        VmSnapshot snapshot = Creating(vm);
        vm.SnapshotCreateSucceeded(snapshot.Id, PendingOf(snapshot), "vm42/s1");
        return snapshot;
    }

    private static Guid PendingOf(VmSnapshot snapshot) => snapshot.Operation.PendingId!.Value;

    // The status, then whether Incus holds the snapshot as far as Titan last knew.
    private static string Shape(VmSnapshot s)
    {
        string held = "-";
        if (s.InstanceStatus != null)
        {
            held = s.InstanceStatus.Value.ToString();
        }
        return $"{s.Status}/{held}";
    }

    // ---------- the create ----------

    [Fact]
    public void ANewSnapshot_IsCreating_WithAnIdAndATime()
    {
        VmSnapshot snapshot = Creating(RunningVm());

        Assert.Equal(VmSnapshotStatus.Creating, snapshot.Status);
        Assert.NotEqual(Guid.Empty, PendingOf(snapshot));
        Assert.True(snapshot.Operation.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Null(snapshot.Operation.LastSeenStatus);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheId_LeavesItAvailable_AndEndsIt_Once()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);
        Guid createId = PendingOf(snapshot);

        bool ended = vm.SnapshotCreateSucceeded(snapshot.Id, createId, "vm42/s1");
        bool again = vm.SnapshotCreateSucceeded(snapshot.Id, createId, "vm42/s9");

        Assert.True(ended);
        Assert.False(again);
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Null(snapshot.Operation.FailureReason);
        Assert.Null(snapshot.Operation.PendingId);
    }

    // Nothing would be left to delete the snapshot through: the report is refused before anything is written.
    [Fact]
    public void Done_OfTheCreate_WithoutAnId_IsRefused_AndWritesNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);
        VmSnapshotOperation before = snapshot.Operation;

        var thrown = Assert.Throws<BusinessException>(() => vm.SnapshotCreateSucceeded(snapshot.Id, PendingOf(snapshot), null));

        Assert.Equal("INVALID_STATUS", thrown.Code);
        Assert.Equal("Creating/-", Shape(snapshot));
        Assert.Equal(before, snapshot.Operation);
        Assert.Equal(VmStatus.Snapshotting, vm.Status);
    }

    [Fact]
    public void Failed_OfTheCreate_RecordsWhy_AndLeavesNothingOnIncus()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        Assert.True(vm.SnapshotCreateFailed(snapshot.Id, PendingOf(snapshot), null, Reason, "raw"));

        Assert.Equal("Failed/-", Shape(snapshot));
        Assert.Equal(Reason, snapshot.Operation.FailureReason);
        Assert.Equal("raw", snapshot.Operation.FailureDetail);
        Assert.NotNull(snapshot.Operation.FailedAt);
        Assert.False(snapshot.CountsTowardQuota);
        Assert.Equal(0, vm.SnapshotQuotaUsage);
    }

    // The give-up found the snapshot on Incus: it is kept with its id, so it can be deleted through Incus.
    [Fact]
    public void Failed_OfTheCreate_CarryingAnId_KeepsTheId_AndLeavesItAvailable()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Creating(vm);

        vm.SnapshotCreateFailed(snapshot.Id, PendingOf(snapshot), "vm42/s1", Reason, "raw");

        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
        Assert.NotNull(snapshot.Operation.FailureReason);
        vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid());
        Assert.Equal(VmSnapshotStatus.Deleting, snapshot.Status);
        Assert.Equal("vm42/s1", snapshot.GetExternalId());
    }

    // ---------- the revert ----------

    [Fact]
    public void Done_OfTheRevert_EndsIt_AndChangesNothingElse()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);
        Assert.Equal(VmSnapshotStatus.Reverting, snapshot.Status);
        Assert.Equal(revert, snapshot.Operation.PendingId);

        bool ended = vm.SnapshotRevertSucceeded(snapshot.Id, revert);

        Assert.True(ended);
        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal("vm42/s1", snapshot.ExternalId);
    }

    [Fact]
    public void Failed_OfTheRevert_RecordsWhy_AndKeepsTheSnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);

        Assert.True(vm.SnapshotRevertFailed(snapshot.Id, revert, Reason, "raw"));

        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal(Reason, snapshot.Operation.FailureReason);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
    }

    // A Done clears the reason an earlier operation left (titan-ops architecture/desired-and-current.md#request-failure).
    [Fact]
    public void Done_OfALaterRequest_ClearsTheOldReason()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid firstRevert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, firstRevert);
        vm.SnapshotRevertFailed(snapshot.Id, firstRevert, Reason, "raw");
        Guid secondRevert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, secondRevert);

        vm.SnapshotRevertSucceeded(snapshot.Id, secondRevert);

        Assert.Null(snapshot.Operation.FailureReason);
        Assert.Null(snapshot.Operation.FailureDetail);
        Assert.Null(snapshot.Operation.FailedAt);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheSnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid delete = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, delete);

        bool ended = vm.SnapshotDeleteSucceeded(snapshot.Id, delete);

        Assert.True(ended);
        Assert.Equal("Deleted/-", Shape(snapshot));
        Assert.True(snapshot.IsDeleted);
        Assert.False(vm.SnapshotDeleteSucceeded(snapshot.Id, delete));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheSnapshotBack_WithWhy_AndTheNextDeleteIsANewRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid first = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, first);
        Assert.Equal(0, vm.SnapshotQuotaUsage);

        Assert.True(vm.SnapshotDeleteFailed(snapshot.Id, first, Reason, "raw"));

        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal(Reason, snapshot.Operation.FailureReason);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
        Guid second = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, second);
        Assert.Equal(second, snapshot.Operation.PendingId);
        Assert.Equal(VmSnapshotStatus.Deleting, snapshot.Status);
    }

    // ---------- where a revert or a delete that ended goes back to ----------

    // The snapshot rests as the reconciler last saw it during the wait: missing ⇒ NotFound, otherwise Available
    // (titan-ops features/vm/design.md#vm-snapshot-reports).
    [Theory]
    [InlineData("RevertSucceeded", "", VmSnapshotStatus.Available)]
    [InlineData("RevertSucceeded", "NotFound", VmSnapshotStatus.NotFound)]
    [InlineData("RevertSucceeded", "NotFound,Observed", VmSnapshotStatus.Available)]
    [InlineData("RevertFailed", "", VmSnapshotStatus.Available)]
    [InlineData("RevertFailed", "NotFound", VmSnapshotStatus.NotFound)]
    [InlineData("RevertFailed", "NotFound,Observed", VmSnapshotStatus.Available)]
    [InlineData("DeleteFailed", "", VmSnapshotStatus.Available)]
    [InlineData("DeleteFailed", "NotFound", VmSnapshotStatus.NotFound)]
    [InlineData("DeleteFailed", "NotFound,Observed", VmSnapshotStatus.Available)]
    public void TheEndOfARevertOrADelete_RestsAsTheReconcilerLastSawTheSnapshot(
        string ending, string seenDuringTheWait, VmSnapshotStatus expected)
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid operation = Guid.NewGuid();
        if (ending == "DeleteFailed")
        {
            vm.DeleteSnapshot(snapshot.Id, operation);
        }
        else
        {
            vm.RevertSnapshot(snapshot.Id, operation);
        }
        Observe(vm, snapshot, seenDuringTheWait);

        bool ended = Report(vm, snapshot.Id, ending, operation);

        Assert.True(ended);
        Assert.Equal(expected, snapshot.Status);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Null(snapshot.Operation.LastSeenStatus);
        Assert.Equal(expected == VmSnapshotStatus.Available, snapshot.CountsTowardQuota);
    }

    // A delete asked of a snapshot Incus lost starts from "not held"; a sighting during the wait gives it back as Available.
    [Theory]
    [InlineData("", VmSnapshotStatus.NotFound)]
    [InlineData("Observed", VmSnapshotStatus.Available)]
    public void AFailedDelete_OfASnapshotIncusLost_RestsAsTheReconcilerLastSawIt(string seenDuringTheWait, VmSnapshotStatus expected)
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        vm.MarkSnapshotNotFound(snapshot.Id);
        Guid delete = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, delete);
        Assert.Null(snapshot.Operation.LastSeenStatus);
        Observe(vm, snapshot, seenDuringTheWait);

        Assert.True(vm.SnapshotDeleteFailed(snapshot.Id, delete, Reason, "raw"));

        Assert.Equal(expected, snapshot.Status);
    }

    private static void Observe(Vm vm, VmSnapshot snapshot, string seen)
    {
        foreach (string observation in seen.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (observation == "NotFound")
            {
                vm.MarkSnapshotNotFound(snapshot.Id);
            }
            else
            {
                vm.MarkSnapshotObserved(snapshot.Id);
            }
        }
    }

    // ---------- only the matching id, to the waiting operation's method, ends an operation ----------

    private static readonly string[] Methods =
        ["CreateSucceeded", "CreateFailed", "RevertSucceeded", "RevertFailed", "DeleteSucceeded", "DeleteFailed"];

    private static VmSnapshotStatus WaitingStatusOf(string method)
    {
        if (method.StartsWith("Create"))
        {
            return VmSnapshotStatus.Creating;
        }
        if (method.StartsWith("Revert"))
        {
            return VmSnapshotStatus.Reverting;
        }
        return VmSnapshotStatus.Deleting;
    }

    private static bool Report(Vm vm, Guid snapshotId, string method, Guid operationId) => method switch
    {
        "CreateSucceeded" => vm.SnapshotCreateSucceeded(snapshotId, operationId, "vm42/s9"),
        "CreateFailed" => vm.SnapshotCreateFailed(snapshotId, operationId, "vm42/s9", "late", "late"),
        "RevertSucceeded" => vm.SnapshotRevertSucceeded(snapshotId, operationId),
        "RevertFailed" => vm.SnapshotRevertFailed(snapshotId, operationId, "late", "late"),
        "DeleteSucceeded" => vm.SnapshotDeleteSucceeded(snapshotId, operationId),
        _ => vm.SnapshotDeleteFailed(snapshotId, operationId, "late", "late"),
    };

    // Every method meets every waiting status twice: with an id no operation has, and with the waiting operation's own
    // id, the second only for the methods of another operation.
    public static TheoryData<string, VmSnapshotStatus, bool> ReportsNamingAnotherOperation()
    {
        var data = new TheoryData<string, VmSnapshotStatus, bool>();
        VmSnapshotStatus[] waiting = [VmSnapshotStatus.Creating, VmSnapshotStatus.Reverting, VmSnapshotStatus.Deleting];
        foreach (string method in Methods)
        {
            foreach (VmSnapshotStatus status in waiting)
            {
                data.Add(method, status, false);
                if (WaitingStatusOf(method) != status)
                {
                    data.Add(method, status, true);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ReportsNamingAnotherOperation))]
    public void AReportNamingAnotherOperation_EndsNothing_AndChangesNothing(string method, VmSnapshotStatus waiting, bool withTheWaitingId)
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot;
        if (waiting == VmSnapshotStatus.Creating)
        {
            snapshot = Creating(vm);
        }
        else
        {
            snapshot = Available(vm);
            if (waiting == VmSnapshotStatus.Reverting)
            {
                vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());
            }
            else
            {
                vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid());
            }
        }
        VmSnapshotOperation before = snapshot.Operation;
        string? idBefore = snapshot.ExternalId;
        VmStatus vmBefore = vm.Status;
        Guid operationId = Guid.NewGuid();
        if (withTheWaitingId)
        {
            operationId = PendingOf(snapshot);
        }

        bool ended = Report(vm, snapshot.Id, method, operationId);

        Assert.False(ended);
        Assert.Equal(waiting, snapshot.Status);
        Assert.Equal(before, snapshot.Operation);
        Assert.Equal(idBefore, snapshot.ExternalId);
        Assert.Equal(vmBefore, vm.Status);
    }

    // The other snapshot's operation id is not this one's.
    [Fact]
    public void AReportNamingAnotherSnapshotsOperation_EndsNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot other = Available(vm);
        VmSnapshot reverting = Available(vm);
        vm.RevertSnapshot(reverting.Id, Guid.NewGuid());
        vm.DeleteSnapshot(other.Id, Guid.NewGuid());

        Assert.False(vm.SnapshotDeleteSucceeded(other.Id, PendingOf(reverting)));
        Assert.False(vm.SnapshotRevertSucceeded(other.Id, PendingOf(reverting)));

        Assert.Equal("Deleting/Available", Shape(other));
        Assert.Equal("Reverting/Available", Shape(reverting));
        Assert.Null(reverting.Operation.FailureReason);
        Assert.Equal(VmStatus.Snapshotting, vm.Status);
    }

    // A snapshot's operation is its own: the VM's report ends the VM's operation and leaves the snapshot's waiting.
    [Fact]
    public void TheVmsReport_DoesNotEndTheSnapshotsRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);

        Assert.False(vm.StopSucceeded(revert, null, null, null));

        Assert.Equal(VmSnapshotStatus.Reverting, snapshot.Status);
    }

    // ---------- observations end nothing ----------

    [Fact]
    public void Observations_RecordWhatIsSeen_AndEndNoRequest()
    {
        Vm vm = RunningVm();
        VmSnapshot deleting = Available(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());
        VmSnapshot reverting = Available(vm);
        vm.RevertSnapshot(reverting.Id, Guid.NewGuid());

        vm.MarkSnapshotObserved(reverting.Id);
        vm.MarkSnapshotNotFound(deleting.Id);

        Assert.Equal("Reverting/Available", Shape(reverting));
        Assert.Equal("Deleting/-", Shape(deleting));

        vm.SnapshotRevertFailed(reverting.Id, PendingOf(reverting), Reason, "raw"); // the VM is at rest for the create below
        VmSnapshot creating = Creating(vm);
        vm.MarkSnapshotObserved(creating.Id);
        Assert.Equal("Creating/Available", Shape(creating));

        vm.MarkSnapshotNotFound(creating.Id);
        Assert.Equal("Creating/-", Shape(creating));
    }

    [Fact]
    public void ADeletedSnapshot_AbsorbsEveryReportAndObservation()
    {
        Vm vm = RunningVm();
        VmSnapshot snapshot = Available(vm);
        Guid delete = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, delete);
        vm.SnapshotDeleteSucceeded(snapshot.Id, delete);

        vm.MarkSnapshotObserved(snapshot.Id);
        vm.MarkSnapshotNotFound(snapshot.Id);
        Assert.False(vm.SnapshotDeleteFailed(snapshot.Id, delete, Reason, "raw"));

        Assert.Equal("Deleted/-", Shape(snapshot));
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid())).Code);
    }

    // ---------- the VM's delete ----------

    // A create or revert in flight touches the instance, so the VM is Snapshotting and its delete is refused like any
    // other waiting operation; a delete of a snapshot waiting does not (titan-ops features/vm/design.md#vm-snapshot-counting).
    [Fact]
    public void TheVmsDelete_WaitsForACreateAndARevert_NotForASnapshotsDelete()
    {
        Vm vm = RunningVm();
        VmSnapshot deleting = Available(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());
        VmSnapshot reverting = Available(vm);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(reverting.Id, revert);

        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);
        vm.SnapshotRevertFailed(reverting.Id, revert, Reason, "raw");
        VmSnapshot creating = Creating(vm);
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);
        vm.SnapshotCreateFailed(creating.Id, PendingOf(creating), null, Reason, "raw");

        vm.Delete(Guid.NewGuid()); // the delete of a snapshot still waits and does not block
        Assert.Equal(VmStatus.Deleting, vm.Status);
    }

    // Incus deletes an instance's snapshots with it: the VM's delete Done ends every snapshot, whatever it waited on.
    [Fact]
    public void TheVmsDeleteDone_EndsEverySnapshot()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Available(vm);
        VmSnapshot deleting = Available(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());
        VmSnapshot failedCreate = Creating(vm);
        vm.SnapshotCreateFailed(failedCreate.Id, PendingOf(failedCreate), null, Reason, "raw");
        Guid vmDelete = Guid.NewGuid();
        vm.Delete(vmDelete);

        bool ended = vm.DeleteSucceeded(vmDelete);

        Assert.True(ended);
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Equal(VmSnapshotStatus.Deleted, s.Status));
        Assert.All(new[] { resting, deleting, failedCreate }, s => Assert.Null(s.Operation.PendingId));
    }

    [Fact]
    public void TheVmsDeleteFailed_LeavesTheSnapshotsAsTheyWere()
    {
        Vm vm = RunningVm();
        VmSnapshot deleting = Available(vm);
        Guid snapshotDelete = Guid.NewGuid();
        vm.DeleteSnapshot(deleting.Id, snapshotDelete);
        Guid vmDelete = Guid.NewGuid();
        vm.Delete(vmDelete);

        bool ended = vm.DeleteFailed(vmDelete, "refused", "raw");

        Assert.True(ended);
        Assert.Equal("Deleting/Available", Shape(deleting));
        Assert.Equal(snapshotDelete, deleting.Operation.PendingId);
    }

    [Fact]
    public void EndWithItsVm_EndsTheSnapshot_WhateverItWaitedOn_AndARepeatChangesNothing()
    {
        Vm vm = RunningVm();
        VmSnapshot resting = Available(vm);
        VmSnapshot deleting = Available(vm);
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());

        resting.EndWithItsVm();
        deleting.EndWithItsVm();
        deleting.EndWithItsVm();

        Assert.True(resting.IsDeleted);
        Assert.True(deleting.IsDeleted);
        Assert.Null(deleting.Operation.PendingId);
        Assert.Equal(VmSnapshotStatus.Deleted, deleting.Status);
    }
}

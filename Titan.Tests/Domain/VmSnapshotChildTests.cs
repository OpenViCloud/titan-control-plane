using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// A VM snapshot is the VM's child entity (titan-ops architecture/code-structure.md#child-entity): the VM counts its
// snapshots toward the per-VM cap, refuses its own delete while a snapshot command still touches the instance, routes
// each request and observation to the snapshot it names, and takes every snapshot with it when its delete is confirmed.
public class VmSnapshotChildTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static Vm RunningVm()
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("vm42", "10.0.0.5");
        return vm;
    }

    private static Tenant TenantWithSnapshotCap(int maxVmSnapshotsPerVm)
    {
        var tenant = TestTenant.Active("t", "kc", "t@x.io");
        tenant.UpdateQuota(TenantQuota.Create(10, 1, 10, maxVmSnapshotsPerVm, 3, 20, 10, 20, 5));
        return tenant;
    }

    // The seven situations the repository count once measured (titan-ops features/vm/design.md#vm-snapshot-counting):
    // creating, held by Incus and reverting count; a delete in flight, a snapshot Incus lost, a Deleted row and a
    // failed create do not.
    private static Vm VmWithEverySnapshotSituation()
    {
        var vm = RunningVm();
        vm.AddSnapshotUnchecked("creating"); // counts
        vm.AddSnapshotUnchecked("ready").MarkCreated("vm42/s1"); // counts
        var reverting = vm.AddSnapshotUnchecked("reverting"); // counts, and blocks the VM's delete
        reverting.MarkCreated("vm42/s2");
        reverting.Revert();
        var leaving = vm.AddSnapshotUnchecked("leaving");
        leaving.MarkCreated("vm42/s3");
        leaving.RequestDelete();
        var lost = vm.AddSnapshotUnchecked("lost");
        lost.MarkCreated("vm42/s4");
        lost.MarkNotFound();
        var gone = vm.AddSnapshotUnchecked("gone");
        gone.MarkCreated("vm42/s5");
        gone.RequestDelete();
        gone.MarkDeleted();
        vm.AddSnapshotUnchecked("failed").MarkRequestFailed("could not create", "raw");
        return vm;
    }

    [Fact]
    public void SnapshotQuotaUsage_CountsCreatingExistingAndReverting_ButNotADeleteInFlight_NotFound_NorAFailedCreate()
    {
        var vm = VmWithEverySnapshotSituation();

        Assert.Equal(7, vm.Snapshots.Count);
        Assert.Equal(3, vm.SnapshotQuotaUsage);
    }

    [Fact]
    public void AddSnapshot_AtTheCap_Refuses_AndAddsNothing()
    {
        var vm = VmWithEverySnapshotSituation(); // usage 3

        var ex = Assert.Throws<BusinessException>(() => vm.AddSnapshot(TenantWithSnapshotCap(3), "one-more"));

        Assert.Equal("VM_SNAPSHOT_QUOTA_EXCEEDED", ex.Code);
        Assert.Equal(7, vm.Snapshots.Count);
    }

    [Fact]
    public void AddSnapshot_OneBelowTheCap_AddsASnapshotWaitingForItsCreate()
    {
        var vm = VmWithEverySnapshotSituation(); // usage 3

        VmSnapshot snapshot = vm.AddSnapshot(TenantWithSnapshotCap(4), "nightly");

        Assert.Contains(snapshot, vm.Snapshots);
        Assert.Equal(vm.Id, snapshot.VmId);
        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal(VmSnapshotTransition.Creating, snapshot.Transition);
        Assert.Equal(4, vm.SnapshotQuotaUsage);
    }

    // The VM says it cannot be snapshotted before anyone counts: a different code from the cap.
    [Theory]
    [InlineData(VmState.Error)]
    [InlineData(null)]
    public void AddSnapshot_OnAVmNotSnapshottable_IsRefusedBeforeTheQuota(VmState? status)
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        if (status != null)
        {
            vm.MarkRunning("vm42", "10.0.0.5");
            vm.MarkObserved(status.Value, null);
        }

        var ex = Assert.Throws<BusinessException>(() => vm.AddSnapshot(TenantWithSnapshotCap(0), "nightly"));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public void AddSnapshot_WhileAStopWaits_IsRefused_AndAddsNothing()
    {
        var vm = RunningVm();
        vm.RequestStop();

        var ex = Assert.Throws<BusinessException>(() => vm.AddSnapshot(TenantWithSnapshotCap(3), "nightly"));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Empty(vm.Snapshots);
    }

    // A snapshot command still touching the instance (create or revert) races the VM's delete on Incus
    // (titan-ops features/vm/design.md#vm-snapshot-counting): the VM refuses, with nothing changed.
    [Theory]
    [InlineData("creating")]
    [InlineData("reverting")]
    public void RequestDelete_WhileASnapshotCreatesOrReverts_IsRefused_AndLeavesTheVmAlone(string blocking)
    {
        var vm = RunningVm();
        var snapshot = vm.AddSnapshotUnchecked(blocking);
        if (blocking == "reverting")
        {
            snapshot.MarkCreated("vm42/s1");
            snapshot.Revert();
        }

        var ex = Assert.Throws<BusinessException>(() => vm.RequestDelete());

        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", ex.Code);
        Assert.Null(vm.PendingIntent);
        Assert.Equal(DesiredExistence.Exists, vm.DesiredExistence);
    }

    // The same refusal on a VM with no provider id: its row must not vanish under the snapshot command.
    [Fact]
    public void RequestDelete_OnAVmWithoutAnId_WhileASnapshotCreates_IsRefused()
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRequestFailed("could not create", "raw"); // never provisioned
        vm.AddSnapshotUnchecked("creating");

        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", Assert.Throws<BusinessException>(() => vm.RequestDelete()).Code);
    }

    // The instance Incus lost (reconciler reported NotFound) still has a revert command waiting: the delete waits too.
    [Fact]
    public void RequestDelete_OnAVmIncusLost_WhileASnapshotReverts_IsRefused()
    {
        var vm = RunningVm();
        var reverting = vm.AddSnapshotUnchecked("reverting");
        reverting.MarkCreated("vm42/s1");
        vm.RevertSnapshot(reverting.Id);
        vm.MarkNotFound();

        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", Assert.Throws<BusinessException>(() => vm.RequestDelete()).Code);
        Assert.Equal(VmSnapshotTransition.Reverting, reverting.Transition);
    }

    // A snapshot at rest, one whose delete waits, one Incus lost, a Deleted row and a failed create do not block:
    // the VM's delete takes them along, and the worker's snapshot delete no-ops once the VM is gone.
    [Fact]
    public void RequestDelete_WithSnapshotsAtRestOrDeleting_IsAccepted()
    {
        var vm = RunningVm();
        vm.AddSnapshotUnchecked("ready").MarkCreated("vm42/s1");
        var leaving = vm.AddSnapshotUnchecked("leaving");
        leaving.MarkCreated("vm42/s2");
        leaving.RequestDelete();
        var lost = vm.AddSnapshotUnchecked("lost");
        lost.MarkCreated("vm42/s3");
        lost.MarkNotFound();
        var gone = vm.AddSnapshotUnchecked("gone");
        gone.MarkCreated("vm42/s4");
        gone.RequestDelete();
        gone.MarkDeleted();
        vm.AddSnapshotUnchecked("failed").MarkRequestFailed("could not create", "raw");

        VmIntentTicket ticket = vm.RequestDelete();

        Assert.Equal(VmIntentOutcome.Dispatch, ticket.Outcome);
        Assert.Equal(VmIntent.Delete, vm.PendingIntent);
    }

    // The VM's delete took its snapshots with it (titan-ops features/vm/design.md#vm-snapshot). Only a snapshot whose own
    // delete was waiting is named: its request is done here, once.
    [Fact]
    public void MarkDeleted_MarksEverySnapshotDeleted_AndNamesOnlyTheSnapshotsWhoseOwnDeleteWasWaiting()
    {
        var vm = RunningVm();
        var ready = vm.AddSnapshotUnchecked("ready");
        ready.MarkCreated("vm42/s1");
        var leaving = vm.AddSnapshotUnchecked("leaving");
        leaving.MarkCreated("vm42/s2");
        leaving.RequestDelete();
        var alreadyGone = vm.AddSnapshotUnchecked("gone");
        alreadyGone.MarkCreated("vm42/s3");
        alreadyGone.RequestDelete();
        alreadyGone.MarkDeleted();
        vm.RequestDelete();

        VmDeletionOutcome outcome = vm.MarkDeleted();

        Assert.Equal(VmIntent.Delete, outcome.Fulfilled);
        Assert.Equal(CurrentExistence.Deleted, vm.CurrentExistence);
        Assert.All(vm.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.All(vm.Snapshots, s => Assert.Null(s.Transition));
        Assert.Equal([leaving.Id], outcome.SnapshotsWhoseDeleteWasFulfilled.Select(s => s.Id));
    }

    [Fact]
    public void MarkDeleted_WithNoSnapshots_NamesNone()
    {
        var vm = RunningVm();
        vm.RequestDelete();

        VmDeletionOutcome outcome = vm.MarkDeleted();

        Assert.Equal(VmIntent.Delete, outcome.Fulfilled);
        Assert.Empty(outcome.SnapshotsWhoseDeleteWasFulfilled);
    }

    // Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    [Fact]
    public void MarkDeleted_Repeated_IsAbsorbed_AndNamesNothingAgain()
    {
        var vm = RunningVm();
        var leaving = vm.AddSnapshotUnchecked("leaving");
        leaving.MarkCreated("vm42/s2");
        leaving.RequestDelete();
        vm.RequestDelete();
        vm.MarkDeleted();

        VmDeletionOutcome repeat = vm.MarkDeleted();

        Assert.Null(repeat.Fulfilled);
        Assert.Empty(repeat.SnapshotsWhoseDeleteWasFulfilled);
    }

    // A snapshot the VM does not have answers like a missing one.
    [Fact]
    public void RequireSnapshot_OfAnotherVmsSnapshot_IsNotFound()
    {
        var vm = RunningVm();
        var other = RunningVm();
        var foreign = other.AddSnapshotUnchecked("theirs");
        vm.AddSnapshotUnchecked("mine");

        Assert.Null(vm.SnapshotOrNull(foreign.Id));
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.RequireSnapshot(foreign.Id)).Code);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.RevertSnapshot(foreign.Id)).Code);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.RequestSnapshotDelete(foreign.Id)).Code);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.MarkSnapshotDeleted(foreign.Id)).Code);
    }

    // A revert checks the VM first, then the snapshot, in one call on the root.
    [Fact]
    public void RevertSnapshot_ChecksTheVm_ThenTheSnapshot_AndYieldsTheSnapshotsId()
    {
        var vm = RunningVm();
        var snapshot = vm.AddSnapshotUnchecked("s");
        snapshot.MarkCreated("vm42/s1");

        vm.RequestStop();
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RevertSnapshot(snapshot.Id)).Code);
        Assert.Null(snapshot.Transition);

        var resting = RunningVm();
        var creating = resting.AddSnapshotUnchecked("creating");
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => resting.RevertSnapshot(creating.Id)).Code);

        var ready = resting.AddSnapshotUnchecked("ready");
        ready.MarkCreated("vm42/s2");
        Assert.Equal("vm42/s2", resting.RevertSnapshot(ready.Id));
        Assert.Equal(VmSnapshotTransition.Reverting, ready.Transition);
    }

    // The root routes to the snapshot and returns what the snapshot returns.
    [Fact]
    public void SnapshotCalls_ReachTheSnapshotNamed_AndReturnItsAnswer()
    {
        var vm = RunningVm();
        var snapshot = vm.AddSnapshotUnchecked("s");
        var untouched = vm.AddSnapshotUnchecked("u");

        Assert.True(vm.MarkSnapshotCreated(snapshot.Id, "vm42/s1"));
        Assert.False(vm.MarkSnapshotObserved(snapshot.Id));
        Assert.Equal("vm42/s1", vm.RevertSnapshot(snapshot.Id));
        Assert.True(vm.MarkSnapshotReverted(snapshot.Id));
        Assert.False(vm.MarkSnapshotReverted(snapshot.Id));

        Assert.Equal("vm42/s1", vm.RequestSnapshotDelete(snapshot.Id));
        Assert.Equal(VmSnapshotTransition.Deleting, snapshot.Transition);
        vm.AbandonSnapshotRequest(snapshot.Id, "refused", "raw");
        Assert.Null(snapshot.Transition);
        Assert.Equal("refused", snapshot.RequestFailureReason);

        vm.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(CurrentExistence.NotFound, snapshot.CurrentExistence);
        vm.RequestSnapshotDelete(snapshot.Id);
        Assert.True(vm.MarkSnapshotDeleted(snapshot.Id));

        Assert.Equal(VmSnapshotTransition.Creating, untouched.Transition);
        Assert.Null(untouched.ExternalId);
    }

    [Fact]
    public void DeleteSnapshotLocally_OnASnapshotWhoseCreateFailed_WritesBothPairs()
    {
        var vm = RunningVm();
        var failed = vm.AddSnapshotUnchecked("failed");
        vm.MarkSnapshotRequestFailed(failed.Id, "reason", "detail");

        vm.DeleteSnapshotLocally(failed.Id);

        Assert.Equal(CurrentExistence.Deleted, failed.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, failed.DesiredExistence);
        Assert.Equal(0, vm.SnapshotQuotaUsage);
    }
}

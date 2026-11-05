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
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;

    private static Vm RunningVm()
    {
        var vm = Vm.Create(TenantIdOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("vm42", "10.0.0.5");
        return vm;
    }

    // The seven situations the repository count once measured (titan-ops features/vm/design.md#vm-snapshot-counting):
    // creating, held by Incus and reverting count; a delete in flight, a snapshot Incus lost, a Deleted row and a
    // failed create do not.
    private static Vm VmWithEverySnapshotSituation()
    {
        var vm = RunningVm();
        vm.WithSnapshot("creating"); // counts
        vm.WithSnapshot("ready").MarkCreated("vm42/s1"); // counts
        var reverting = vm.WithSnapshot("reverting"); // counts, and blocks the VM's delete
        reverting.MarkCreated("vm42/s2");
        reverting.Revert(Guid.NewGuid());
        var leaving = vm.WithSnapshot("leaving");
        leaving.MarkCreated("vm42/s3");
        leaving.Delete(Guid.NewGuid());
        var lost = vm.WithSnapshot("lost");
        lost.MarkCreated("vm42/s4");
        lost.MarkNotFound();
        var gone = vm.WithSnapshot("gone");
        gone.MarkCreated("vm42/s5");
        gone.Delete(Guid.NewGuid());
        gone.MarkDeleted();
        vm.WithSnapshot("failed").MarkRequestFailed("could not create", "raw");
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
    public void AddSnapshot_OneBelowTheCap_AddsASnapshotWaitingForItsCreate()
    {
        var vm = VmWithEverySnapshotSituation(); // usage 3

        VmSnapshot snapshot = vm.WithSnapshot("nightly");

        Assert.Contains(snapshot, vm.Snapshots);
        Assert.Equal(vm.Id, snapshot.VmId);
        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal(VmSnapshotTransition.Creating, snapshot.Transition);
        Assert.Equal(4, vm.SnapshotQuotaUsage);
    }

    [Theory]
    [InlineData(VmStatus.Error)]
    [InlineData(null)]
    public void AddSnapshot_OnAVmNotSnapshottable_IsRefused(VmStatus? status)
    {
        var vm = Vm.Create(TenantIdOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        if (status != null)
        {
            vm.MarkRunning("vm42", "10.0.0.5");
            vm.MarkObserved(status.Value, null);
        }

        var ex = Assert.Throws<BusinessException>(() => vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "nightly"));

        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public void AddSnapshot_WhileAStopWaits_IsRefused_AndAddsNothing()
    {
        var vm = RunningVm();
        vm.Stop(Guid.NewGuid());

        var ex = Assert.Throws<BusinessException>(() => vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "nightly"));

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
        var snapshot = vm.WithSnapshot(blocking);
        if (blocking == "reverting")
        {
            snapshot.MarkCreated("vm42/s1");
            snapshot.Revert(Guid.NewGuid());
        }

        var ex = Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid()));

        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", ex.Code);
        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Running, vm.Status);
    }

    // The instance Incus lost (reconciler reported NotFound) still has a revert command waiting: the delete waits too.
    [Fact]
    public void RequestDelete_OnAVmIncusLost_WhileASnapshotReverts_IsRefused()
    {
        var vm = RunningVm();
        var reverting = vm.WithSnapshot("reverting");
        reverting.MarkCreated("vm42/s1");
        vm.RevertSnapshot(reverting.Id, Guid.NewGuid());
        vm.MarkNotFound();

        Assert.Equal("VM_SNAPSHOT_IN_PROGRESS", Assert.Throws<BusinessException>(() => vm.Delete(Guid.NewGuid())).Code);
        Assert.Equal(VmSnapshotTransition.Reverting, reverting.Transition);
    }

    // A snapshot at rest, one whose delete waits, one Incus lost, a Deleted row and a failed create do not block:
    // the VM's delete takes them along, and the worker's snapshot delete no-ops once the VM is gone.
    [Fact]
    public void RequestDelete_WithSnapshotsAtRestOrDeleting_IsAccepted()
    {
        var vm = RunningVm();
        vm.WithSnapshot("ready").MarkCreated("vm42/s1");
        var leaving = vm.WithSnapshot("leaving");
        leaving.MarkCreated("vm42/s2");
        leaving.Delete(Guid.NewGuid());
        var lost = vm.WithSnapshot("lost");
        lost.MarkCreated("vm42/s3");
        lost.MarkNotFound();
        var gone = vm.WithSnapshot("gone");
        gone.MarkCreated("vm42/s4");
        gone.Delete(Guid.NewGuid());
        gone.MarkDeleted();
        vm.WithSnapshot("failed").MarkRequestFailed("could not create", "raw");

        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        Assert.Equal(delete, vm.PendingOperationId);
        Assert.Equal(VmStatus.Deleting, vm.Status);
    }

    // The VM's delete took its snapshots with it (titan-ops features/vm/design.md#vm-snapshot). A snapshot's own waiting
    // delete ends with it; which snapshots waited is read before the call, by VmReports.
    [Fact]
    public void MarkDeleted_MarksEverySnapshotDeleted_AndEndsTheirWaitingOperations()
    {
        var vm = RunningVm();
        var ready = vm.WithSnapshot("ready");
        ready.MarkCreated("vm42/s1");
        var leaving = vm.WithSnapshot("leaving");
        leaving.MarkCreated("vm42/s2");
        leaving.Delete(Guid.NewGuid());
        var alreadyGone = vm.WithSnapshot("gone");
        alreadyGone.MarkCreated("vm42/s3");
        alreadyGone.Delete(Guid.NewGuid());
        alreadyGone.MarkDeleted();
        vm.Delete(Guid.NewGuid());

        bool outcome = vm.MarkDeleted();

        Assert.True(outcome);
        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.All(vm.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.All(vm.Snapshots, s => Assert.Null(s.Transition));
    }

    [Fact]
    public void MarkDeleted_WithNoSnapshots_EndsTheDelete()
    {
        var vm = RunningVm();
        vm.Delete(Guid.NewGuid());

        bool outcome = vm.MarkDeleted();

        Assert.True(outcome);
        Assert.Equal(VmStatus.Deleted, vm.Status);
    }

    // Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    [Fact]
    public void MarkDeleted_Repeated_IsAbsorbed()
    {
        var vm = RunningVm();
        var leaving = vm.WithSnapshot("leaving");
        leaving.MarkCreated("vm42/s2");
        leaving.Delete(Guid.NewGuid());
        vm.Delete(Guid.NewGuid());
        vm.MarkDeleted();

        bool repeat = vm.MarkDeleted();

        Assert.False(repeat); // nothing waits on a Deleted VM, so the report ends nothing
    }

    // A snapshot the VM does not have answers like a missing one.
    [Fact]
    public void ASnapshotOfAnotherVm_IsNotFound()
    {
        var vm = RunningVm();
        var other = RunningVm();
        var foreign = other.WithSnapshot("theirs");
        vm.WithSnapshot("mine");

        Assert.DoesNotContain(vm.Snapshots, s => s.Id == foreign.Id);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.RevertSnapshot(foreign.Id, Guid.NewGuid())).Code);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.DeleteSnapshot(foreign.Id, Guid.NewGuid())).Code);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => vm.FinishSnapshotOperation(foreign.Id, foreign.Pending!.Id, "vm42/s9")).Code);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.MarkSnapshotObserved(foreign.Id)).Code);
        Assert.Equal("VM_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => vm.MarkSnapshotNotFound(foreign.Id)).Code);
    }

    // A revert checks the VM first, then the snapshot, in one call on the root.
    [Fact]
    public void RevertSnapshot_ChecksTheVm_ThenTheSnapshot_AndWaitsOnTheRevertOfTheSnapshotOnIncus()
    {
        var vm = RunningVm();
        var snapshot = vm.WithSnapshot("s");
        snapshot.MarkCreated("vm42/s1");

        vm.Stop(Guid.NewGuid());
        Assert.Equal("VM_INVALID_STATUS", Assert.Throws<BusinessException>(() => vm.RevertSnapshot(snapshot.Id, Guid.NewGuid())).Code);
        Assert.Null(snapshot.Transition);

        var resting = RunningVm();
        var creating = resting.WithSnapshot("creating");
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => resting.RevertSnapshot(creating.Id, Guid.NewGuid())).Code);

        var ready = resting.WithSnapshot("ready");
        ready.MarkCreated("vm42/s2");
        Guid revert = Guid.NewGuid();
        resting.RevertSnapshot(ready.Id, revert);
        Assert.Equal("vm42/s2", ready.GetExternalId());
        Assert.Equal(revert, ready.PendingOperationId);
        Assert.Equal(VmSnapshotTransition.Reverting, ready.Transition);
    }

    // The root routes each call to the snapshot it names; a report returns what the snapshot returns.
    [Fact]
    public void SnapshotCalls_ReachTheSnapshotNamed_AndReturnItsAnswer()
    {
        var vm = RunningVm();
        var snapshot = vm.WithSnapshot("s");
        var untouched = vm.WithSnapshot("u");

        Assert.Equal(VmSnapshotOperationKind.Create,
            vm.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, "vm42/s1"));
        vm.MarkSnapshotObserved(snapshot.Id);
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);
        Assert.Equal("vm42/s1", snapshot.GetExternalId());
        Assert.Equal(VmSnapshotOperationKind.Revert,
            vm.FinishSnapshotOperation(snapshot.Id, revert, null));
        Assert.Null(vm.FinishSnapshotOperation(snapshot.Id, revert, null));

        Guid delete = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, delete);
        Assert.Equal("vm42/s1", snapshot.GetExternalId());
        Assert.Equal(VmSnapshotTransition.Deleting, snapshot.Transition);
        vm.FailSnapshotOperation(snapshot.Id, delete, null, "refused", "raw");
        Assert.Null(snapshot.Transition);
        Assert.Equal("refused", snapshot.OperationFailureReason);

        vm.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(CurrentExistence.NotFound, snapshot.CurrentExistence);
        Guid again = Guid.NewGuid();
        vm.DeleteSnapshot(snapshot.Id, again);
        Assert.NotNull(vm.FinishSnapshotOperation(snapshot.Id, again, null));
        Assert.True(snapshot.IsDeleted);

        Assert.Equal(VmSnapshotTransition.Creating, untouched.Transition);
        Assert.Null(untouched.ExternalId);
    }

    [Fact]
    public void RequestSnapshotDelete_OfASnapshotWhoseCreateFailed_WritesBothPairs_AndWaitsOnNothing()
    {
        var vm = RunningVm();
        var failed = vm.WithSnapshot("failed");
        vm.FailSnapshotOperation(failed.Id, failed.Pending!.Id, null, "reason", "detail");

        vm.DeleteSnapshot(failed.Id, Guid.NewGuid());

        Assert.True(failed.IsDeleted);
        Assert.Null(failed.PendingOperationId);
        Assert.Equal(CurrentExistence.Deleted, failed.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, failed.DesiredExistence);
        Assert.Equal(0, vm.SnapshotQuotaUsage);
    }
}

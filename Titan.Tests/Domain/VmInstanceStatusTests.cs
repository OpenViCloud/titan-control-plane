using Titan.Domain.Aggregates.Vm;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// InstanceStatus is the Incus status the VM holds, worked out from Status and the status seen while an operation waits
// (titan-ops features/vm/design.md#vm-status). Nothing stores it: it is read off Status and Operation.LastSeenStatus.
public class VmInstanceStatusTests
{
    private static Vm CreatedVm()
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        return vm;
    }

    private static Vm RestingVm(VmStatus status)
    {
        Vm vm = CreatedVm();
        vm.MarkRunning("vm42", "10.0.0.5");
        if (status != VmStatus.Running)
        {
            vm.MarkObserved(status, "10.0.0.5", null);
        }
        return vm;
    }

    [Theory]
    [InlineData(VmStatus.Running)]
    [InlineData(VmStatus.Ready)]
    [InlineData(VmStatus.Stopped)]
    [InlineData(VmStatus.Frozen)]
    [InlineData(VmStatus.Error)]
    public void AtRest_IsTheStatus_AndNothingElseIsKept(VmStatus status)
    {
        Vm vm = RestingVm(status);

        Assert.Equal(status, vm.Status);
        Assert.Equal(status, vm.InstanceStatus);
        Assert.Null(vm.Operation.LastSeenStatus);
    }

    [Theory]
    [InlineData(VmStatus.Running, true)]
    [InlineData(VmStatus.Ready, true)]
    [InlineData(VmStatus.Frozen, true)]
    [InlineData(VmStatus.Error, true)]
    [InlineData(VmStatus.Stopped, false)]
    public void AVmAtRest_IsBilledForTheIncusStatusesThatHoldResources(VmStatus status, bool billed)
    {
        Assert.Equal(billed, RestingVm(status).IsBilled);
    }

    [Fact]
    public void ACreateThatWaits_HoldsNone()
    {
        Vm vm = CreatedVm();

        Assert.Equal(VmStatus.Creating, vm.Status);
        Assert.Null(vm.InstanceStatus);
    }

    [Theory]
    [InlineData(VmStatus.Unknown)]
    [InlineData(VmStatus.NotFound)]
    public void WhenNoStatusIsKnown_IsNone(VmStatus status)
    {
        Vm vm = RestingVm(VmStatus.Running);
        if (status == VmStatus.Unknown)
        {
            vm.Stop(Guid.NewGuid());
            vm.StopSucceeded(vm.Operation.PendingId!.Value, null, null, null);
        }
        else
        {
            vm.MarkNotFound();
        }

        Assert.Equal(status, vm.Status);
        Assert.Null(vm.InstanceStatus);
    }

    [Fact]
    public void WhileAStopWaits_IsTheStatusItWasAskedFrom_AndTheVmIsStillBilled()
    {
        Vm vm = RestingVm(VmStatus.Running);

        vm.Stop(Guid.NewGuid());

        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.True(vm.IsBilled);
    }

    [Fact]
    public void WhileAStartWaitsFromFrozen_TheVmIsBilled_FromStopped_NotBilled()
    {
        Vm frozen = RestingVm(VmStatus.Frozen);
        Vm stopped = RestingVm(VmStatus.Stopped);

        frozen.Start(Guid.NewGuid());
        stopped.Start(Guid.NewGuid());

        Assert.Equal(VmStatus.Frozen, frozen.InstanceStatus);
        Assert.True(frozen.IsBilled);
        Assert.Equal(VmStatus.Stopped, stopped.InstanceStatus);
        Assert.False(stopped.IsBilled);
    }

    [Fact]
    public void AnObservationWhileAnOperationWaits_ChangesTheStatusSeen_NotTheStatus()
    {
        Vm vm = RestingVm(VmStatus.Running);
        vm.Stop(Guid.NewGuid());

        vm.MarkObserved(VmStatus.Stopped, null, null);

        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
        Assert.Equal(VmStatus.Stopped, vm.Operation.LastSeenStatus);
        Assert.False(vm.IsBilled);
    }

    [Fact]
    public void AnObservationAtRest_WritesTheStatus_AndKeepsNoStatusSeen()
    {
        Vm vm = RestingVm(VmStatus.Running);
        vm.Stop(Guid.NewGuid());
        vm.MarkObserved(VmStatus.Stopped, null, null);
        vm.StopSucceeded(vm.Operation.PendingId!.Value, VmStatus.Stopped, null, null);

        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);

        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal(VmStatus.Running, vm.InstanceStatus);
        Assert.Null(vm.Operation.LastSeenStatus);
    }

    [Fact]
    public void AnOperationThatGivesUp_ReturnsToTheStatusSeen_AndForgetsIt()
    {
        Vm vm = RestingVm(VmStatus.Frozen);
        vm.Start(Guid.NewGuid());

        vm.StartFailed(vm.Operation.PendingId!.Value, "NO_ROOM", "raw");

        Assert.Equal(VmStatus.Frozen, vm.Status);
        Assert.Null(vm.Operation.LastSeenStatus);
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal("NO_ROOM", vm.Operation.FailureReason);
    }

    [Fact]
    public void ASnapshotThatEnds_ReturnsToTheStatusItStartedFrom_AndForgetsIt()
    {
        Vm vm = RestingVm(VmStatus.Stopped);
        VmSnapshot snapshot = vm.WithSnapshot("s");

        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);
        Assert.Null(vm.Operation.PendingId);

        vm.MarkSnapshotCreated(snapshot.Id, "vm42/s1");

        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Null(vm.Operation.LastSeenStatus);
    }

    [Fact]
    public void ASnapshotThatEnds_KeepsTheLastFailureOfAnOperation()
    {
        Vm vm = RestingVm(VmStatus.Running);
        vm.Stop(Guid.NewGuid());
        vm.StopFailed(vm.Operation.PendingId!.Value, "NO_ROOM", "raw");
        VmSnapshot snapshot = vm.WithSnapshot("s");

        vm.MarkSnapshotCreated(snapshot.Id, "vm42/s1");

        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal("NO_ROOM", vm.Operation.FailureReason);
        Assert.Equal("raw", vm.Operation.FailureDetail);
        Assert.NotNull(vm.Operation.FailedAt);
    }

    // Before this change a status seen while the create waited stayed on the row when the create gave up, and the Failed VM
    // was billed. Only a worker that observes an instance Titan has no id for can reach it.
    [Fact]
    public void ACreateThatGaveUpWithNoInstance_HoldsNone_EvenIfAnObservationCameFirst()
    {
        Vm vm = CreatedVm();
        vm.MarkObserved(VmStatus.Running, "10.0.0.5", null);

        vm.CreateFailed(vm.Operation.PendingId!.Value, null, "refused", null);

        Assert.Equal(VmStatus.Failed, vm.Status);
        Assert.Null(vm.InstanceStatus);
        Assert.False(vm.IsBilled);
    }

    [Fact]
    public void ADeletedVm_HoldsNone()
    {
        Vm vm = RestingVm(VmStatus.Running);
        vm.Delete(Guid.NewGuid());
        vm.DeleteSucceeded(vm.Operation.PendingId!.Value);

        Assert.Equal(VmStatus.Deleted, vm.Status);
        Assert.Null(vm.InstanceStatus);
        Assert.False(vm.IsBilled);
    }
}

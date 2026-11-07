using System.Reflection;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// A snapshot create or revert makes the VM Snapshotting until its command reports
// (titan-ops features/vm/design.md#vm-snapshot): the status says what is running, so Start, Stop, Delete, a second
// snapshot, a volume attach and a shell wait, all through VM_INVALID_STATUS.
public class VmSnapshottingTests
{
    private static Vm RestingVm(VmStatus status = VmStatus.Running)
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("vm42", "10.0.0.5");
        if (status != VmStatus.Running)
        {
            vm.MarkObserved(status, "10.0.0.5", null);
        }
        return vm;
    }

    private static Vm SnapshottingVm(out VmSnapshot snapshot, VmStatus status = VmStatus.Running)
    {
        Vm vm = RestingVm(status);
        snapshot = vm.WithSnapshot("s");
        return vm;
    }

    private static Vm VmWithARestingSnapshot(out Guid snapshotId)
    {
        Vm vm = RestingVm();
        VmSnapshot snapshot = vm.WithSnapshot("s");
        vm.MarkSnapshotCreated(snapshot.Id, "vm42/s1");
        snapshotId = snapshot.Id;
        return vm;
    }

    private static string CodeOf(Action act) => Assert.Throws<BusinessException>(act).Code;

    [Theory]
    [InlineData(VmStatus.Running)]
    [InlineData(VmStatus.Ready)]
    [InlineData(VmStatus.Stopped)]
    [InlineData(VmStatus.Frozen)]
    public void AddSnapshot_FromAnIncusWordIncusSnapshotsFrom_MakesTheVmSnapshotting_WithoutAnOperationOfItsOwn(VmStatus status)
    {
        Vm vm = SnapshottingVm(out VmSnapshot snapshot, status);

        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal(status, vm.InstanceStatus);
        Assert.Equal(VmSnapshotTransition.Creating, snapshot.Transition);
    }

    [Theory]
    [InlineData(VmStatus.Running)]
    [InlineData(VmStatus.Ready)]
    [InlineData(VmStatus.Stopped)]
    [InlineData(VmStatus.Frozen)]
    public void RevertSnapshot_FromAnIncusWordIncusSnapshotsFrom_MakesTheVmSnapshotting(VmStatus status)
    {
        Vm vm = RestingVm(status);
        VmSnapshot snapshot = vm.WithSnapshot("s");
        vm.MarkSnapshotCreated(snapshot.Id, "vm42/s1");
        Assert.Equal(status, vm.Status);

        vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());

        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        Assert.Null(vm.Operation.PendingId);
        Assert.Equal(VmSnapshotTransition.Reverting, snapshot.Transition);
    }

    [Fact]
    public void AddSnapshot_Refused_LeavesTheStatusAlone()
    {
        Vm vm = RestingVm(VmStatus.Error);

        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s")));
        Assert.Equal("INVALID_REQUEST", CodeOf(() => RestingVm().AddSnapshot(Guid.NewGuid(), Guid.Empty, "s")));

        Assert.Equal(VmStatus.Error, vm.Status);
        Assert.Empty(vm.Snapshots);
    }

    [Fact]
    public void RevertSnapshot_OfASnapshotNotRestingOnIncus_Throws_AndTheVmStaysAtRest()
    {
        Vm vm = VmWithARestingSnapshot(out Guid snapshotId);
        vm.DeleteSnapshot(snapshotId, Guid.NewGuid()); // a delete waits: nothing to revert to

        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", CodeOf(() => vm.RevertSnapshot(snapshotId, Guid.NewGuid())));

        Assert.Equal(VmStatus.Running, vm.Status);
    }

    [Fact]
    public void ASnapshottingVm_RefusesEveryOperationThatWouldRaceTheSnapshotCommand()
    {
        Vm vm = VmWithARestingSnapshot(out Guid resting);
        VmSnapshot creating = vm.WithSnapshot("next"); // the VM is Snapshotting from here

        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.Stop(Guid.NewGuid())));
        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.Start(Guid.NewGuid())));
        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.Delete(Guid.NewGuid())));
        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "third")));
        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.RevertSnapshot(resting, Guid.NewGuid())));
        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.EnsureVolumeCanAttach()));
        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.EnsureShellCanOpen()));

        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        Assert.Equal(2, vm.Snapshots.Count);
        Assert.Equal(VmSnapshotTransition.Creating, creating.Transition);
    }

    [Fact]
    public void ASnapshottingVm_StillAcceptsALoadBalancerBackend()
    {
        Vm vm = SnapshottingVm(out _);

        vm.EnsureLoadBalancerCanSendTo(vm.NetworkId);

        Assert.Equal("vm42", vm.ExternalId);
    }

    [Fact]
    public void ASnapshottingVm_StaysSnapshotting_WhateverTheReconcilerObserves()
    {
        Vm vm = SnapshottingVm(out _);

        vm.MarkObserved(VmStatus.Stopped, null, null);
        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.InstanceStatus);

        vm.MarkNotFound();
        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        Assert.Null(vm.InstanceStatus);
    }

    [Fact]
    public void ASnapshottingVm_StillOccupiesItsQuotaNetworkAndInstance_AndIsBilledByTheWordIncusHolds()
    {
        Vm vm = SnapshottingVm(out _);

        Assert.True(vm.OccupiesQuota);
        Assert.True(vm.HasNetwork);
        Assert.True(vm.HasAnInstance);
        Assert.True(vm.IsBilled);
    }

    [Fact]
    public void AStartingVm_RefusesASnapshotAndARevert_ButAcceptsAVolume()
    {
        Vm vm = RestingVm(VmStatus.Stopped);
        VmSnapshot snapshot = vm.WithSnapshot("s");
        vm.MarkSnapshotCreated(snapshot.Id, "vm42/s1");
        vm.Start(Guid.NewGuid());
        Assert.Equal(VmStatus.Starting, vm.Status);

        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s2")));
        Assert.Equal("VM_INVALID_STATUS", CodeOf(() => vm.RevertSnapshot(snapshot.Id, Guid.NewGuid())));
        vm.EnsureVolumeCanAttach();

        Assert.Equal(VmStatus.Starting, vm.Status);
        Assert.Single(vm.Snapshots);
    }

    [Theory] // the status the reconciler read last is the status the VM goes back to, Done or Failed
    [InlineData(VmStatus.Running, true)]
    [InlineData(VmStatus.Ready, true)]
    [InlineData(VmStatus.Stopped, true)]
    [InlineData(VmStatus.Frozen, true)]
    [InlineData(VmStatus.Error, true)]
    [InlineData(VmStatus.Running, false)]
    [InlineData(VmStatus.Ready, false)]
    [InlineData(VmStatus.Stopped, false)]
    [InlineData(VmStatus.Frozen, false)]
    [InlineData(VmStatus.Error, false)]
    public void TheCreatesDoneAndFailed_PutTheStatusBackToTheWordIncusHolds(VmStatus observed, bool done)
    {
        Vm vm = SnapshottingVm(out VmSnapshot snapshot);
        vm.MarkObserved(observed, null, null);
        Guid operation = snapshot.Pending!.Id;

        VmSnapshotOperationKind? kind = done
            ? vm.FinishSnapshotOperation(snapshot.Id, operation, "vm42/s1")
            : vm.FailSnapshotOperation(snapshot.Id, operation, null, "Creating the snapshot failed.", "raw");

        Assert.Equal(VmSnapshotOperationKind.Create, kind);
        Assert.Equal(observed, vm.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheRevertsDoneAndFailed_PutTheStatusBackToTheWordIncusHolds(bool done)
    {
        Vm vm = RestingVm(VmStatus.Stopped);
        VmSnapshot snapshot = vm.WithSnapshot("s");
        vm.MarkSnapshotCreated(snapshot.Id, "vm42/s1");
        vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());
        Guid operation = snapshot.Pending!.Id;

        VmSnapshotOperationKind? kind = done
            ? vm.FinishSnapshotOperation(snapshot.Id, operation, null)
            : vm.FailSnapshotOperation(snapshot.Id, operation, null, "Reverting the snapshot failed.", "raw");

        Assert.Equal(VmSnapshotOperationKind.Revert, kind);
        Assert.Equal(VmStatus.Stopped, vm.Status);
    }

    [Fact]
    public void WhenTheReconcilerFoundNoInstanceDuringTheSnapshot_TheReportLeavesTheVmUnknown()
    {
        Vm vm = SnapshottingVm(out VmSnapshot snapshot);
        vm.MarkNotFound();

        vm.FailSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, null, "Creating the snapshot failed.", "raw");

        Assert.Equal(VmStatus.Unknown, vm.Status);
    }

    [Fact]
    public void AWordObservedDuringTheSnapshot_IsTheWordTheVmReturnsTo()
    {
        Vm vm = SnapshottingVm(out VmSnapshot snapshot);
        vm.MarkObserved(VmStatus.Stopped, null, null);

        vm.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, "vm42/s1");

        Assert.Equal(VmStatus.Stopped, vm.Status);
    }

    [Fact]
    public void ADeleteSnapshot_NeverChangesTheVmStatus_NeitherAskedNorEnded()
    {
        Vm vm = VmWithARestingSnapshot(out Guid snapshotId);
        VmSnapshot snapshot = vm.Snapshots.Single();

        vm.DeleteSnapshot(snapshotId, Guid.NewGuid());
        Assert.Equal(VmStatus.Running, vm.Status);

        vm.FinishSnapshotOperation(snapshotId, snapshot.Pending!.Id, null);
        Assert.Equal(VmStatus.Running, vm.Status);
    }

    [Fact]
    public void ADeleteSnapshotDone_WhileTheVmIsSnapshotting_LeavesItSnapshotting()
    {
        Vm vm = VmWithARestingSnapshot(out Guid first);
        vm.DeleteSnapshot(first, Guid.NewGuid());
        VmSnapshot next = vm.WithSnapshot("next"); // the VM is Snapshotting while the delete of the first still waits

        vm.FinishSnapshotOperation(first, vm.Snapshots.Single(s => s.Id == first).Pending!.Id, null);

        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        Assert.Equal(VmSnapshotTransition.Creating, next.Transition);
    }

    [Fact] // a snapshot command ends without touching a status it did not set (the VM here is Stopped, not Snapshotting)
    public void ASnapshotCommandWaitingOnAVmThatIsNotSnapshotting_EndsWithoutChangingTheStatus()
    {
        Vm vm = SnapshottingVm(out VmSnapshot snapshot);
        typeof(Vm).GetProperty(nameof(Vm.Status), BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(vm, VmStatus.Stopped);

        vm.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, "vm42/s1");

        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Null(snapshot.Transition);
    }

    [Fact] // the invariant the status stands for: Snapshotting exactly while one create or revert waits
    public void Snapshotting_IsExactlyWhileOneCreateOrRevertWaits_AcrossTheWholeLifeOfASnapshot()
    {
        Vm vm = RestingVm();
        Assert.False(Holds(vm));

        VmSnapshot snapshot = vm.WithSnapshot("s");
        Assert.True(Holds(vm));
        vm.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, "vm42/s1");
        Assert.False(Holds(vm));

        vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());
        Assert.True(Holds(vm));
        vm.FailSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, null, "Reverting the snapshot failed.", "raw");
        Assert.False(Holds(vm));

        vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid());
        Assert.False(Holds(vm));
        vm.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, null);
        Assert.False(Holds(vm));
    }

    private static bool Holds(Vm vm)
    {
        int waiting = vm.Snapshots.Count(s =>
            s.Pending is { Kind: VmSnapshotOperationKind.Create or VmSnapshotOperationKind.Revert });
        Assert.InRange(waiting, 0, 1);
        Assert.Equal(waiting == 1, vm.Status == VmStatus.Snapshotting);
        return vm.Status == VmStatus.Snapshotting;
    }
}

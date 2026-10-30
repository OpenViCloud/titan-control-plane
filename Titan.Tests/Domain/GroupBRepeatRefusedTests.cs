using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat. The aggregate refuses every request it cannot accept — a repeat and an already-done
// request included — and the refusal leaves the state exactly as it was. Answering a repeat like the
// first request is the HANDLER's job; those tests sit with the handlers (*ServiceTests).
public class GroupBRepeatRefusedTests
{
    private static Vm RunningVm()
    {
        var vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl", "cluster-a", 20);
        vm.MarkRunning("vm-ext", "10.0.0.5");
        return vm;
    }

    private static Network ActiveNetwork()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "net-1", "cluster-a");
        network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    private static Volume AllocatedVolume()
    {
        var volume = Volume.CreateUnchecked(Guid.NewGuid(), "vol-1", "cluster-a", "std", 10);
        volume.MarkCreated("vol-ext");
        return volume;
    }

    [Fact]
    public void NetworkDelete_Repeated_IsRefused_AndKeepsTheDeleteWaiting()
    {
        var network = ActiveNetwork();
        network.RequestDelete();

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => network.RequestDelete()).Code);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void VolumeDelete_Repeated_IsRefused_AndStaysDeleting()
    {
        var volume = AllocatedVolume();
        volume.RequestDelete();

        Assert.Equal("VOLUME_INVALID_STATE", Assert.Throws<BusinessException>(() => volume.RequestDelete()).Code);
        Assert.Equal(VolumeTransition.Deleting, volume.Transition);
    }

    [Fact]
    public void VolumeAttach_ToTheSameVm_Repeated_IsRefused_AndKeepsTheAttachment()
    {
        var volume = AllocatedVolume();
        var vm = RunningVm();
        volume.Attach(vm);

        Assert.Equal("VOLUME_INVALID_STATE", Assert.Throws<BusinessException>(() => volume.Attach(vm)).Code);
        Assert.Equal(VolumeTransition.Attaching, volume.Transition);
        Assert.Equal(vm.Id, volume.DesiredAttachedVmId);
    }

    [Fact]
    public void VolumeAttach_ToAnotherVm_IsRefused_AndKeepsTheFirst()
    {
        var volume = AllocatedVolume();
        var first = RunningVm();
        volume.Attach(first);

        Assert.Equal("VOLUME_INVALID_STATE", Assert.Throws<BusinessException>(() => volume.Attach(RunningVm())).Code);
        Assert.Equal(first.Id, volume.DesiredAttachedVmId);
    }

    [Fact]
    public void VolumeDetach_Repeated_IsRefused_AndStaysDetaching()
    {
        var volume = AllocatedVolume();
        var vm = RunningVm();
        volume.Attach(vm);
        volume.MarkObserved(vm.Id, false);
        volume.Detach();

        Assert.Equal("VOLUME_INVALID_STATE", Assert.Throws<BusinessException>(() => volume.Detach()).Code);
        Assert.Equal(VolumeTransition.Detaching, volume.Transition);
    }

    [Fact]
    public void VmSnapshotRevert_WhileReverting_IsRefused()
    {
        var vm = RunningVm();
        var snapshot = vm.AddSnapshotUnchecked("snap-1");
        snapshot.MarkCreated("snap-ext");
        vm.RevertSnapshot(snapshot.Id);

        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => vm.RevertSnapshot(snapshot.Id)).Code);
        Assert.Equal(VmSnapshotTransition.Reverting, snapshot.Transition);
    }

    [Fact] // once the revert finished, reverting again is a new request and is accepted
    public void VmSnapshotRevert_AfterItFinished_IsANewRequest()
    {
        var vm = RunningVm();
        var snapshot = vm.AddSnapshotUnchecked("snap-1");
        snapshot.MarkCreated("snap-ext");
        vm.RevertSnapshot(snapshot.Id);
        snapshot.MarkReverted();

        Assert.Equal("snap-ext", vm.RevertSnapshot(snapshot.Id));
    }

    [Fact]
    public void VolumeSnapshotDelete_Repeated_IsRefused_AndStaysDeleting()
    {
        var snapshot = VolumeSnapshot.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vsnap-1");
        snapshot.MarkCreated("vsnap-ext");
        snapshot.RequestDelete();

        Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", Assert.Throws<BusinessException>(() => snapshot.RequestDelete()).Code);
        Assert.Equal(VolumeSnapshotTransition.Deleting, snapshot.Transition);
    }
}

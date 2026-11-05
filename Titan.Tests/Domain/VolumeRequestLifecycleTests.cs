using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a volume and its snapshots: one waiting request each (the
// snapshot's independent of the volume's), ended only by the report carrying its id; an observation writes current and
// ends nothing; a failure puts desired back to current. VolumeStateMachineTests keeps what a request may do from each
// situation; this file is about how a request ends, and what a VM's delete does to the volumes it holds.
public class VolumeRequestLifecycleTests
{
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    private static Volume NewVolume() => Volume.Create(Tenant, "data", "cluster-a", "vol-standard", 10);

    private static Volume RestingVolume()
    {
        Volume volume = NewVolume();
        volume.FinishOperation(volume.Pending!.Id, "vol-ext", false, null, false);
        return volume;
    }

    private static Vm RunningVm()
    {
        Vm vm = Vm.Create(Tenant, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        return vm;
    }

    private static (Volume Volume, Vm Vm) AttachedVolume()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);
        volume.FinishOperation(attach.OperationId, null, true, vm.Id, false);
        return (volume, vm);
    }

    private static VolumeOperationKind? Failed(Volume volume, Guid operationId,
        bool sawTheVolume = false, Guid? attachedVmId = null, bool attachedElsewhere = false, string? externalId = null) =>
        volume.FailOperation(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere,
            "The volume request could not be completed.", "raw");

    // ---------- the create ----------

    [Fact]
    public void ANewVolume_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        Volume volume = NewVolume();

        Assert.Equal(VolumeOperationKind.Create, volume.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, volume.Pending.Id);
        Assert.True(volume.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(VolumeTransition.Creating, volume.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdAndExistence_AndEndsIt()
    {
        Volume volume = NewVolume();
        Guid createId = volume.Pending!.Id;

        VolumeOperationKind? ended = volume.FinishOperation(createId, "vol-ext", false, null, false);

        Assert.Equal(VolumeOperationKind.Create, ended);
        Assert.Null(volume.Pending);
        Assert.Equal(("vol-ext", CurrentExistence.Exists), (volume.ExternalId, volume.CurrentExistence));
    }

    [Fact] // Incus made the volume but the command gave up: it exists, and its create failed
    public void Failed_OfTheCreate_WithAnId_KeepsTheIdAndExistence_AndRecordsTheReason()
    {
        Volume volume = NewVolume();

        Failed(volume, volume.Pending!.Id, externalId: "vol-ext");

        Assert.Null(volume.Pending);
        Assert.Equal(("vol-ext", CurrentExistence.Exists), (volume.ExternalId, volume.CurrentExistence));
        Assert.Equal(DesiredExistence.Exists, volume.DesiredExistence);
        Assert.Equal("The volume request could not be completed.", volume.OperationFailureReason);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_LeavesItNotCreated_WithTheReason()
    {
        Volume volume = NewVolume();

        Failed(volume, volume.Pending!.Id);

        Assert.Null(volume.Pending);
        Assert.Null(volume.ExternalId);
        Assert.Equal(CurrentExistence.NotCreated, volume.CurrentExistence);
        Assert.NotNull(volume.OperationFailedAt);
    }

    // ---------- only the report with the id ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsNull()
    {
        Volume volume = NewVolume();
        Guid createId = volume.Pending!.Id;

        VolumeOperationKind? ended = volume.FinishOperation(Guid.NewGuid(), "vol-ext", false, null, false);

        Assert.Null(ended);
        Assert.Equal(createId, volume.Pending!.Id);
        Assert.Null(volume.ExternalId);
    }

    [Fact]
    public void TheSameDone_Twice_EndsTheRequestOnce()
    {
        Volume volume = NewVolume();
        Guid createId = volume.Pending!.Id;

        Assert.NotNull(volume.FinishOperation(createId, "vol-ext", false, null, false));
        Assert.Null(volume.FinishOperation(createId, "other", false, null, false));

        Assert.Equal("vol-ext", volume.ExternalId);
    }

    [Fact]
    public void AStaleGiveUp_AfterANewRequest_IsIgnored()
    {
        (Volume volume, _) = AttachedVolume();
        OperationTicket firstDetach = volume.Detach();
        Failed(volume, firstDetach.OperationId, true, volume.CurrentAttachedVmId, false);
        OperationTicket secondDetach = volume.Detach();

        VolumeOperationKind? stale = Failed(volume, firstDetach.OperationId);

        Assert.Null(stale);
        Assert.Equal((VolumeOperationKind.Detach, secondDetach.OperationId), (volume.Pending!.Kind, volume.Pending.Id));
    }

    // ---------- attach and detach ----------

    [Fact]
    public void Done_OfAnAttach_RecordsWhatIncusShows_AndEndsIt()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        VolumeOperationKind? ended = volume.FinishOperation(attach.OperationId, null, true, vm.Id, false);

        Assert.Equal(VolumeOperationKind.Attach, ended);
        Assert.Null(volume.Pending);
        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.Equal((volume.ExternalId, vm.ExternalId), (attach.ExternalVolumeId, attach.ExternalVmId));
    }

    [Fact]
    public void Failed_OfAnAttach_PutsTheDesiredAttachmentBackToWhatIncusShows_WithTheReason()
    {
        Volume volume = RestingVolume();
        VolumeAttachProviderIds attach = volume.Attach(RunningVm());

        Failed(volume, attach.OperationId, true, null, false);

        Assert.Null(volume.Pending);
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Equal("The volume request could not be completed.", volume.OperationFailureReason);
    }

    [Fact]
    public void Failed_OfADetach_LeavesItAttached_WithTheReason()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        OperationTicket detach = volume.Detach();

        Failed(volume, detach.OperationId, true, vm.Id, false);

        Assert.Null(volume.Pending);
        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.NotNull(volume.OperationFailureReason);
    }

    [Fact]
    public void Done_ClearsTheReasonAnEarlierRequestLeft()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        OperationTicket failedDetach = volume.Detach();
        Failed(volume, failedDetach.OperationId, true, vm.Id, false);
        OperationTicket detach = volume.Detach();

        volume.FinishOperation(detach.OperationId, null, true, null, false);

        Assert.Null(volume.OperationFailureReason);
        Assert.Null(volume.CurrentAttachedVmId);
    }

    [Fact]
    public void AnObservation_ReachingWhatAnAttachAsked_LeavesTheAttachWaiting()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        volume.MarkObserved(vm.Id, false);

        Assert.Equal((VolumeOperationKind.Attach, attach.OperationId), (volume.Pending!.Kind, volume.Pending.Id));
        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
    }

    [Fact]
    public void NotFound_WhileADeleteWaits_KeepsTheDeleteWaiting()
    {
        Volume volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;

        volume.MarkNotFound();

        Assert.Equal(CurrentExistence.NotFound, volume.CurrentExistence);
        Assert.Equal(delete.OperationId, volume.Pending!.Id);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheVolume_AndTakesItsSnapshots()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot waitingDelete = AddRestingSnapshot(volume);
        VolumeSnapshot failedCreate = volume.WithSnapshot("never");
        volume.FailSnapshotOperation(failedCreate.Id, failedCreate.Pending!.Id, null, "r", "d");
        volume.DeleteSnapshot(waitingDelete.Id);
        // A volume with a live snapshot refuses its delete; this one's snapshots no longer count.
        OperationTicket delete = volume.Delete()!;

        VolumeOperationKind? ended = volume.FinishOperation(delete.OperationId, null, false, null, false);

        Assert.Equal(VolumeOperationKind.Delete, ended);
        Assert.Equal(CurrentExistence.Deleted, volume.CurrentExistence);
        Assert.All(volume.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheVolumeBack_WithTheReason_AndAnotherPressIsANewRequest()
    {
        Volume volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;

        Failed(volume, delete.OperationId);

        Assert.Null(volume.Pending);
        Assert.Equal(DesiredExistence.Exists, volume.DesiredExistence);
        Assert.NotEqual(delete.OperationId, volume.Delete()!.OperationId);
    }

    [Fact]
    public void ADeletedVolume_AbsorbsEveryReportAndObservation()
    {
        Volume volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;
        volume.FinishOperation(delete.OperationId, null, false, null, false);

        Assert.Null(Failed(volume, delete.OperationId));
        volume.MarkObserved(Guid.NewGuid(), true);
        volume.MarkNotFound();

        Assert.Equal(CurrentExistence.Deleted, volume.CurrentExistence);
        Assert.Null(volume.CurrentAttachedVmId);
    }

    // ---------- a snapshot's own request ----------

    private static VolumeSnapshot AddRestingSnapshot(Volume volume)
    {
        VolumeSnapshot snapshot = volume.WithSnapshot($"snap-{Guid.NewGuid():N}"[..10]);
        volume.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, $"vol-ext/{snapshot.Id:N}");
        return snapshot;
    }

    [Fact]
    public void ANewSnapshot_WaitsOnItsOwnCreate_IndependentOfTheVolume()
    {
        Volume volume = RestingVolume();

        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        Assert.Equal(VolumeSnapshotOperationKind.Create, snapshot.Pending!.Kind);
        Assert.Equal(VolumeSnapshotTransition.Creating, snapshot.Transition);
        Assert.Null(volume.Pending);
    }

    [Fact]
    public void Done_OfASnapshotCreate_RecordsTheId_AndOnlyThatIdEndsIt()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        Guid createId = snapshot.Pending!.Id;

        Assert.Null(volume.FinishSnapshotOperation(snapshot.Id, Guid.NewGuid(), "vol-ext/s1"));
        VolumeSnapshotOperationKind? ended =
            volume.FinishSnapshotOperation(snapshot.Id, createId, "vol-ext/s1");

        Assert.Equal(VolumeSnapshotOperationKind.Create, ended);
        Assert.Null(snapshot.Pending);
        Assert.Equal(("vol-ext/s1", CurrentExistence.Exists), (snapshot.ExternalId, snapshot.CurrentExistence));
    }

    [Fact]
    public void AnObservation_OfASnapshotBeingCreated_LeavesTheCreateWaiting()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        volume.MarkSnapshotObserved(snapshot.Id);
        Assert.Equal(VolumeSnapshotOperationKind.Create, snapshot.Pending!.Kind);
        volume.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(VolumeSnapshotOperationKind.Create, snapshot.Pending!.Kind);
    }

    [Fact]
    public void Failed_OfASnapshotDelete_GivesItBack_WithTheReason()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;

        volume.FailSnapshotOperation(snapshot.Id, delete.OperationId, null, "could not", "raw");

        Assert.Null(snapshot.Pending);
        Assert.Equal(DesiredExistence.Exists, snapshot.DesiredExistence);
        Assert.Equal("could not", snapshot.OperationFailureReason);
    }

    [Fact]
    public void Done_OfASnapshotDelete_EndsTheSnapshot_AndTheVolumeKeepsNothingWaiting()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;

        volume.FinishSnapshotOperation(snapshot.Id, delete.OperationId, null);

        Assert.Equal(CurrentExistence.Deleted, snapshot.CurrentExistence);
        Assert.Null(volume.Pending);
    }

    // ---------- a VM's delete is not a request of the volume ----------

    [Fact]
    public void AVmDelete_ReleasesAVolumeWithNothingWaiting_WithoutARequestOfItsOwn()
    {
        (Volume volume, Vm vm) = AttachedVolume();

        volume.ReleaseFromVmDelete(vm.Id);

        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.Pending);
        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
    }

    [Fact]
    public void AVmDelete_LeavesAVolumesOwnWaitingAttach_AsItWas()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        volume.ReleaseFromVmDelete(vm.Id);
        volume.MarkDetachedByVmDelete(vm.Id);

        Assert.Equal((VolumeOperationKind.Attach, attach.OperationId), (volume.Pending!.Kind, volume.Pending.Id));
        Assert.Equal(vm.Id, volume.DesiredAttachedVmId);
    }

    [Fact]
    public void TheVmsDeleteDone_LeavesTheVolumeResting_Unattached_AndDeletable()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        volume.ReleaseFromVmDelete(vm.Id);

        volume.MarkDetachedByVmDelete(vm.Id);
        volume.ReleaseFromVmDelete(vm.Id);

        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.Pending);
        volume.Delete();
        Assert.Equal(VolumeOperationKind.Delete, volume.Pending!.Kind);
    }

    [Fact]
    public void TheVmsDeleteFailed_KeepsTheAttachment_WithNothingWaiting()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        volume.ReleaseFromVmDelete(vm.Id);

        volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);

        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.Null(volume.Pending);
    }

    [Fact]
    public void TheVmsDeleteFailed_LeavesAVolumesOwnWaitingDetach_AsItWas()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        OperationTicket detach = volume.Detach();

        volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);

        Assert.Equal((VolumeOperationKind.Detach, detach.OperationId), (volume.Pending!.Kind, volume.Pending.Id));
        Assert.Null(volume.DesiredAttachedVmId);
    }
}

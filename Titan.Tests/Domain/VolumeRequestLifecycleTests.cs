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
        volume.EndRequest(volume.Pending!.Id, VolumeRequestEnd.Done("vol-ext", null));
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
        volume.EndRequest(attach.RequestId, VolumeRequestEnd.Done(null, new VolumeObservation(vm.Id, false)));
        return (volume, vm);
    }

    private static VolumeRequestEnd Failed(VolumeObservation? observed = null, string? externalId = null) =>
        VolumeRequestEnd.Failed(externalId, observed, "The volume request could not be completed.", "raw");

    // ---------- the create ----------

    [Fact]
    public void ANewVolume_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        Volume volume = NewVolume();

        Assert.Equal(VolumeRequestKind.Create, volume.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, volume.Pending.Id);
        Assert.True(volume.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(VolumeTransition.Creating, volume.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdAndExistence_AndEndsIt()
    {
        Volume volume = NewVolume();
        Guid createId = volume.Pending!.Id;

        VolumeRequestEnded? ended = volume.EndRequest(createId, VolumeRequestEnd.Done("vol-ext", null));

        Assert.Equal((VolumeRequestKind.Create, createId), (ended!.Request.Kind, ended.Request.Id));
        Assert.Null(volume.Pending);
        Assert.Equal(("vol-ext", CurrentExistence.Exists), (volume.ExternalId, volume.CurrentExistence));
    }

    [Fact] // Incus made the volume but the command gave up: it exists, and its create failed
    public void Failed_OfTheCreate_WithAnId_KeepsTheIdAndExistence_AndRecordsTheReason()
    {
        Volume volume = NewVolume();

        volume.EndRequest(volume.Pending!.Id, Failed(externalId: "vol-ext"));

        Assert.Null(volume.Pending);
        Assert.Equal(("vol-ext", CurrentExistence.Exists), (volume.ExternalId, volume.CurrentExistence));
        Assert.Equal(DesiredExistence.Exists, volume.DesiredExistence);
        Assert.Equal("The volume request could not be completed.", volume.RequestFailureReason);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_LeavesItNotCreated_WithTheReason()
    {
        Volume volume = NewVolume();

        volume.EndRequest(volume.Pending!.Id, Failed());

        Assert.Null(volume.Pending);
        Assert.Null(volume.ExternalId);
        Assert.Equal(CurrentExistence.NotCreated, volume.CurrentExistence);
        Assert.NotNull(volume.RequestFailedAt);
    }

    // ---------- only the report with the id ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsNull()
    {
        Volume volume = NewVolume();
        Guid createId = volume.Pending!.Id;

        VolumeRequestEnded? ended = volume.EndRequest(Guid.NewGuid(), VolumeRequestEnd.Done("vol-ext", null));

        Assert.Null(ended);
        Assert.Equal(createId, volume.Pending!.Id);
        Assert.Null(volume.ExternalId);
    }

    [Fact]
    public void TheSameDone_Twice_EndsTheRequestOnce()
    {
        Volume volume = NewVolume();
        Guid createId = volume.Pending!.Id;

        Assert.NotNull(volume.EndRequest(createId, VolumeRequestEnd.Done("vol-ext", null)));
        Assert.Null(volume.EndRequest(createId, VolumeRequestEnd.Done("other", null)));

        Assert.Equal("vol-ext", volume.ExternalId);
    }

    [Fact]
    public void AStaleGiveUp_AfterANewRequest_IsIgnored()
    {
        (Volume volume, _) = AttachedVolume();
        RequestTicket firstDetach = volume.Detach();
        volume.EndRequest(firstDetach.RequestId, Failed(new VolumeObservation(volume.CurrentAttachedVmId, false)));
        RequestTicket secondDetach = volume.Detach();

        VolumeRequestEnded? stale = volume.EndRequest(firstDetach.RequestId, Failed());

        Assert.Null(stale);
        Assert.Equal((VolumeRequestKind.Detach, secondDetach.RequestId), (volume.Pending!.Kind, volume.Pending.Id));
    }

    // ---------- attach and detach ----------

    [Fact]
    public void Done_OfAnAttach_RecordsWhatIncusShows_AndEndsIt()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        VolumeRequestEnded? ended = volume.EndRequest(attach.RequestId,
            VolumeRequestEnd.Done(null, new VolumeObservation(vm.Id, false)));

        Assert.Equal(VolumeRequestKind.Attach, ended!.Request.Kind);
        Assert.Null(volume.Pending);
        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.Equal((volume.ExternalId, vm.ExternalId), (attach.ExternalVolumeId, attach.ExternalVmId));
    }

    [Fact]
    public void Failed_OfAnAttach_PutsTheDesiredAttachmentBackToWhatIncusShows_WithTheReason()
    {
        Volume volume = RestingVolume();
        VolumeAttachProviderIds attach = volume.Attach(RunningVm());

        volume.EndRequest(attach.RequestId, Failed(new VolumeObservation(null, false)));

        Assert.Null(volume.Pending);
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Equal("The volume request could not be completed.", volume.RequestFailureReason);
    }

    [Fact]
    public void Failed_OfADetach_LeavesItAttached_WithTheReason()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        RequestTicket detach = volume.Detach();

        volume.EndRequest(detach.RequestId, Failed(new VolumeObservation(vm.Id, false)));

        Assert.Null(volume.Pending);
        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.NotNull(volume.RequestFailureReason);
    }

    [Fact]
    public void Done_ClearsTheReasonAnEarlierRequestLeft()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        RequestTicket failedDetach = volume.Detach();
        volume.EndRequest(failedDetach.RequestId, Failed(new VolumeObservation(vm.Id, false)));
        RequestTicket detach = volume.Detach();

        volume.EndRequest(detach.RequestId, VolumeRequestEnd.Done(null, new VolumeObservation(null, false)));

        Assert.Null(volume.RequestFailureReason);
        Assert.Null(volume.CurrentAttachedVmId);
    }

    [Fact]
    public void AnObservation_ReachingWhatAnAttachAsked_LeavesTheAttachWaiting()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        volume.MarkObserved(vm.Id, false);

        Assert.Equal((VolumeRequestKind.Attach, attach.RequestId), (volume.Pending!.Kind, volume.Pending.Id));
        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
    }

    [Fact]
    public void NotFound_WhileADeleteWaits_KeepsTheDeleteWaiting()
    {
        Volume volume = RestingVolume();
        RequestTicket delete = volume.RequestDelete()!;

        volume.MarkNotFound();

        Assert.Equal(CurrentExistence.NotFound, volume.CurrentExistence);
        Assert.Equal(delete.RequestId, volume.Pending!.Id);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheVolume_TakesItsSnapshots_AndNamesTheOnesWhoseDeleteWaited()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot waitingDelete = AddRestingSnapshot(volume);
        VolumeSnapshot failedCreate = volume.AddSnapshot("never").Snapshot;
        volume.EndSnapshotRequest(failedCreate.Id, failedCreate.Pending!.Id, VolumeSnapshotRequestEnd.Failed(null, "r", "d"));
        volume.RequestSnapshotDelete(waitingDelete.Id);
        // A volume with a live snapshot refuses its delete; this one's snapshots no longer count.
        RequestTicket delete = volume.RequestDelete()!;

        VolumeRequestEnded? ended = volume.EndRequest(delete.RequestId, VolumeRequestEnd.Done(null, null));

        Assert.Equal(VolumeRequestKind.Delete, ended!.Request.Kind);
        Assert.Equal(CurrentExistence.Deleted, volume.CurrentExistence);
        Assert.All(volume.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.Equal([waitingDelete.Id], ended.SnapshotsWhoseDeleteEnded.Select(s => s.Id));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheVolumeBack_WithTheReason_AndAnotherPressIsANewRequest()
    {
        Volume volume = RestingVolume();
        RequestTicket delete = volume.RequestDelete()!;

        volume.EndRequest(delete.RequestId, Failed());

        Assert.Null(volume.Pending);
        Assert.Equal(DesiredExistence.Exists, volume.DesiredExistence);
        Assert.NotEqual(delete.RequestId, volume.RequestDelete()!.RequestId);
    }

    [Fact]
    public void ADeletedVolume_AbsorbsEveryReportAndObservation()
    {
        Volume volume = RestingVolume();
        RequestTicket delete = volume.RequestDelete()!;
        volume.EndRequest(delete.RequestId, VolumeRequestEnd.Done(null, null));

        Assert.Null(volume.EndRequest(delete.RequestId, Failed()));
        volume.MarkObserved(Guid.NewGuid(), true);
        volume.MarkNotFound();

        Assert.Equal(CurrentExistence.Deleted, volume.CurrentExistence);
        Assert.Null(volume.CurrentAttachedVmId);
    }

    // ---------- a snapshot's own request ----------

    private static VolumeSnapshot AddRestingSnapshot(Volume volume)
    {
        VolumeSnapshot snapshot = volume.AddSnapshot($"snap-{Guid.NewGuid():N}"[..10]).Snapshot;
        volume.EndSnapshotRequest(snapshot.Id, snapshot.Pending!.Id, VolumeSnapshotRequestEnd.Done($"vol-ext/{snapshot.Id:N}"));
        return snapshot;
    }

    [Fact]
    public void ANewSnapshot_WaitsOnItsOwnCreate_IndependentOfTheVolume()
    {
        Volume volume = RestingVolume();

        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;

        Assert.Equal(VolumeSnapshotRequestKind.Create, snapshot.Pending!.Kind);
        Assert.Equal(VolumeSnapshotTransition.Creating, snapshot.Transition);
        Assert.Null(volume.Pending);
    }

    [Fact]
    public void Done_OfASnapshotCreate_RecordsTheId_AndOnlyThatIdEndsIt()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;
        Guid createId = snapshot.Pending!.Id;

        Assert.Null(volume.EndSnapshotRequest(snapshot.Id, Guid.NewGuid(), VolumeSnapshotRequestEnd.Done("vol-ext/s1")));
        PendingRequest<VolumeSnapshotRequestKind>? ended =
            volume.EndSnapshotRequest(snapshot.Id, createId, VolumeSnapshotRequestEnd.Done("vol-ext/s1"));

        Assert.Equal(createId, ended!.Id);
        Assert.Null(snapshot.Pending);
        Assert.Equal(("vol-ext/s1", CurrentExistence.Exists), (snapshot.ExternalId, snapshot.CurrentExistence));
    }

    [Fact]
    public void AnObservation_OfASnapshotBeingCreated_LeavesTheCreateWaiting()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;

        volume.MarkSnapshotObserved(snapshot.Id);
        Assert.Equal(VolumeSnapshotRequestKind.Create, snapshot.Pending!.Kind);
        volume.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(VolumeSnapshotRequestKind.Create, snapshot.Pending!.Kind);
    }

    [Fact]
    public void Failed_OfASnapshotDelete_GivesItBack_WithTheReason()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        RequestTicket delete = volume.RequestSnapshotDelete(snapshot.Id)!;

        volume.EndSnapshotRequest(snapshot.Id, delete.RequestId, VolumeSnapshotRequestEnd.Failed(null, "could not", "raw"));

        Assert.Null(snapshot.Pending);
        Assert.Equal(DesiredExistence.Exists, snapshot.DesiredExistence);
        Assert.Equal("could not", snapshot.RequestFailureReason);
    }

    [Fact]
    public void Done_OfASnapshotDelete_EndsTheSnapshot_AndTheVolumeKeepsNothingWaiting()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        RequestTicket delete = volume.RequestSnapshotDelete(snapshot.Id)!;

        volume.EndSnapshotRequest(snapshot.Id, delete.RequestId, VolumeSnapshotRequestEnd.Done(null));

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

        Assert.Equal((VolumeRequestKind.Attach, attach.RequestId), (volume.Pending!.Kind, volume.Pending.Id));
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
        volume.RequestDelete();
        Assert.Equal(VolumeRequestKind.Delete, volume.Pending!.Kind);
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
        RequestTicket detach = volume.Detach();

        volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);

        Assert.Equal((VolumeRequestKind.Detach, detach.RequestId), (volume.Pending!.Kind, volume.Pending.Id));
        Assert.Null(volume.DesiredAttachedVmId);
    }
}

using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a volume and its snapshots: one waiting operation each (the
// snapshot's independent of the volume's), ended only by the report carrying its id to the method of the operation the
// status says is waiting; an observation records what is seen and ends nothing; each operation's Succeeded and Failed
// have their effect (titan-ops features/volume/design.md#volume-reports). VolumeStateMachineTests keeps what an
// operation may do from each status; this file is about how an operation ends, and what a VM's delete does to the volumes
// it holds.
public class VolumeRequestLifecycleTests
{
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    private static Volume NewVolume() => Volume.Create(Tenant, "data", "cluster-a", "vol-standard", 10);

    private static Guid PendingOf(Volume volume) => volume.Operation.PendingId!.Value;

    private static Guid PendingOf(VolumeSnapshot snapshot) => snapshot.Operation.PendingId!.Value;

    private static Volume RestingVolume()
    {
        Volume volume = NewVolume();
        volume.CreateSucceeded(PendingOf(volume), "vol-ext", false, null, false);
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
        volume.AttachSucceeded(attach.OperationId, null, true, vm.Id, false);
        return (volume, vm);
    }

    // The give-up the worker sends for the operation the volume waits on, whichever it is.
    private static bool Failed(Volume volume, Guid operationId,
        bool sawTheVolume = false, Guid? attachedVmId = null, bool attachedElsewhere = false, string? externalId = null)
    {
        const string reason = "The volume request could not be completed.";
        switch (volume.Status)
        {
            case VolumeStatus.Creating:
                return volume.CreateFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere, reason, "raw");
            case VolumeStatus.Attaching:
                return volume.AttachFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere, reason, "raw");
            case VolumeStatus.Detaching:
                return volume.DetachFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere, reason, "raw");
            case VolumeStatus.Deleting:
                return volume.DeleteFailed(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere, reason, "raw");
            default:
                throw new InvalidOperationException($"No operation waits on a volume that is {volume.Status}.");
        }
    }

    // The status, then whether Incus holds the volume as far as Titan last knew.
    private static string Shape(Volume v)
    {
        string held = "-";
        if (v.InstanceStatus != null)
        {
            held = v.InstanceStatus.Value.ToString();
        }
        return $"{v.Status}/{held}";
    }

    private static string Shape(VolumeSnapshot s)
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
    public void ANewVolume_IsCreating_WithAnIdAndATime()
    {
        Volume volume = NewVolume();

        Assert.Equal(VolumeStatus.Creating, volume.Status);
        Assert.NotEqual(Guid.Empty, PendingOf(volume));
        Assert.True(volume.Operation.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Null(volume.Operation.LastSeenStatus);
        Assert.Equal("Creating/-", Shape(volume));
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdAndStatus_AndEndsIt()
    {
        Volume volume = NewVolume();
        Guid createId = PendingOf(volume);

        bool ended = volume.CreateSucceeded(createId, "vol-ext", false, null, false);

        Assert.True(ended);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("vol-ext", volume.ExternalId);
        Assert.Equal("Available/Available", Shape(volume));
    }

    // Nothing would be left to delete the volume through: the report is refused before anything is written.
    [Fact]
    public void Done_OfTheCreate_WithoutAnId_IsRefused_AndWritesNothing()
    {
        Volume volume = NewVolume();
        VolumeOperation before = volume.Operation;

        var thrown = Assert.Throws<BusinessException>(() => volume.CreateSucceeded(PendingOf(volume), null, true, Guid.NewGuid(), false));

        Assert.Equal("INVALID_STATUS", thrown.Code);
        Assert.Equal("Creating/-", Shape(volume));
        Assert.Equal(before, volume.Operation);
        Assert.Null(volume.ExternalId);
        Assert.Null(volume.CurrentAttachedVmId);
    }

    [Fact] // Incus made the volume but the command gave up: it exists, and its create failed
    public void Failed_OfTheCreate_WithAnId_KeepsTheIdAndStatus_AndRecordsTheReason()
    {
        Volume volume = NewVolume();

        Assert.True(Failed(volume, PendingOf(volume), externalId: "vol-ext"));

        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("vol-ext", volume.ExternalId);
        Assert.Equal("Available/Available", Shape(volume));
        Assert.Equal("The volume request could not be completed.", volume.Operation.FailureReason);
        Assert.Equal("raw", volume.Operation.FailureDetail);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_LeavesItFailed_WithTheReason()
    {
        Volume volume = NewVolume();

        Assert.True(Failed(volume, PendingOf(volume)));

        Assert.Null(volume.Operation.PendingId);
        Assert.Null(volume.ExternalId);
        Assert.Equal("Failed/-", Shape(volume));
        Assert.NotNull(volume.Operation.FailedAt);
        Assert.False(volume.OccupiesQuota);
        Assert.False(volume.IsBilled);
    }

    // Without a provider id nothing could delete the volume through Incus: whatever the worker read, it is Failed.
    [Fact]
    public void Failed_OfTheCreate_ThatReadAVolumeButBroughtNoId_IsFailed_AndNotBilled()
    {
        Volume volume = NewVolume();

        Assert.True(Failed(volume, PendingOf(volume), sawTheVolume: true));

        Assert.Equal("Failed/-", Shape(volume));
        Assert.Null(volume.ExternalId);
        Assert.False(volume.IsBilled);
        Assert.False(volume.OccupiesQuota);
    }

    // A create that waits is Creating, which always counts; what the reconciler sees changes only whether Incus is held
    // to hold it, which bills.
    [Fact]
    public void ACreateThatWaits_CountsTowardTheQuota_WhateverTheReconcilerSees()
    {
        Volume volume = NewVolume();

        volume.MarkNotFound();
        Assert.True(volume.OccupiesQuota);
        Assert.False(volume.IsBilled);

        volume.MarkObserved(null, false);
        Assert.True(volume.OccupiesQuota);
        Assert.True(volume.IsBilled);
        Assert.Equal(VolumeStatus.Creating, volume.Status);
    }

    // A volume whose create gave up before Incus held it is Failed: the reconciler does not list it, and a late
    // observation does not make it exist.
    [Fact]
    public void AnObservation_OfAFailedCreate_IsIgnored()
    {
        Volume volume = NewVolume();
        Failed(volume, PendingOf(volume));
        VolumeOperation before = volume.Operation;

        volume.MarkObserved(Guid.NewGuid(), true);
        volume.MarkNotFound();

        Assert.Equal("Failed/-", Shape(volume));
        Assert.Equal(before, volume.Operation);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.False(volume.CurrentAttachedElsewhere);
    }

    // ---------- only the report with the id ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsFalse()
    {
        Volume volume = NewVolume();
        Guid createId = PendingOf(volume);

        bool ended = volume.CreateSucceeded(Guid.NewGuid(), "vol-ext", false, null, false);

        Assert.False(ended);
        Assert.Equal(createId, PendingOf(volume));
        Assert.Null(volume.ExternalId);
        Assert.Equal(VolumeStatus.Creating, volume.Status);
    }

    [Fact]
    public void TheSameDone_Twice_EndsTheRequestOnce()
    {
        Volume volume = NewVolume();
        Guid createId = PendingOf(volume);

        Assert.True(volume.CreateSucceeded(createId, "vol-ext", false, null, false));
        Assert.False(volume.CreateSucceeded(createId, "other", false, null, false));

        Assert.Equal("vol-ext", volume.ExternalId);
    }

    [Fact]
    public void AStaleGiveUp_AfterANewRequest_IsIgnored()
    {
        (Volume volume, _) = AttachedVolume();
        OperationTicket firstDetach = volume.Detach();
        Failed(volume, firstDetach.OperationId, true, volume.CurrentAttachedVmId, false);
        OperationTicket secondDetach = volume.Detach();

        bool stale = Failed(volume, firstDetach.OperationId);

        Assert.False(stale);
        Assert.Equal(VolumeStatus.Detaching, volume.Status);
        Assert.Equal(secondDetach.OperationId, PendingOf(volume));
    }

    private static readonly string[] Methods =
    [
        "CreateSucceeded", "CreateFailed", "AttachSucceeded", "AttachFailed",
        "DetachSucceeded", "DetachFailed", "DeleteSucceeded", "DeleteFailed"
    ];

    private static VolumeStatus WaitingStatusOf(string method)
    {
        if (method.StartsWith("Create"))
        {
            return VolumeStatus.Creating;
        }
        if (method.StartsWith("Attach"))
        {
            return VolumeStatus.Attaching;
        }
        if (method.StartsWith("Detach"))
        {
            return VolumeStatus.Detaching;
        }
        return VolumeStatus.Deleting;
    }

    // The report of one method; sees: the worker read the volume (with the id, used by a VM), or did not.
    private static bool Report(Volume volume, string method, Guid operationId, bool sees = true)
    {
        string? externalId = null;
        Guid? attachedVmId = null;
        if (sees)
        {
            externalId = "late-ext";
            attachedVmId = Guid.NewGuid();
        }
        return method switch
        {
            "CreateSucceeded" => volume.CreateSucceeded(operationId, externalId, sees, attachedVmId, false),
            "CreateFailed" => volume.CreateFailed(operationId, externalId, sees, attachedVmId, false, "late", "late"),
            "AttachSucceeded" => volume.AttachSucceeded(operationId, externalId, sees, attachedVmId, false),
            "AttachFailed" => volume.AttachFailed(operationId, externalId, sees, attachedVmId, false, "late", "late"),
            "DetachSucceeded" => volume.DetachSucceeded(operationId, externalId, sees, attachedVmId, false),
            "DetachFailed" => volume.DetachFailed(operationId, externalId, sees, attachedVmId, false, "late", "late"),
            "DeleteSucceeded" => volume.DeleteSucceeded(operationId, externalId, sees, attachedVmId, false),
            _ => volume.DeleteFailed(operationId, externalId, sees, attachedVmId, false, "late", "late"),
        };
    }

    private static Volume Waiting(VolumeStatus status)
    {
        if (status == VolumeStatus.Creating)
        {
            return NewVolume();
        }
        if (status == VolumeStatus.Attaching)
        {
            Volume attaching = RestingVolume();
            attaching.Attach(RunningVm());
            return attaching;
        }
        if (status == VolumeStatus.Detaching)
        {
            (Volume detaching, _) = AttachedVolume();
            detaching.Detach();
            return detaching;
        }
        Volume deleting = RestingVolume();
        deleting.Delete();
        return deleting;
    }

    // Every method meets every waiting status twice: with an id no operation has, and with the waiting operation's own
    // id, the second only for the methods of another operation.
    public static TheoryData<string, VolumeStatus, bool> ReportsNamingAnotherOperation()
    {
        var data = new TheoryData<string, VolumeStatus, bool>();
        VolumeStatus[] waiting = [VolumeStatus.Creating, VolumeStatus.Attaching, VolumeStatus.Detaching, VolumeStatus.Deleting];
        foreach (string method in Methods)
        {
            foreach (VolumeStatus status in waiting)
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
    public void AReportNamingAnotherOperation_EndsNothing_AndChangesNothing(string method, VolumeStatus waiting, bool withTheWaitingId)
    {
        Volume volume = Waiting(waiting);
        VolumeOperation before = volume.Operation;
        string? idBefore = volume.ExternalId;
        Guid? currentVmBefore = volume.CurrentAttachedVmId;
        bool elsewhereBefore = volume.CurrentAttachedElsewhere;
        Guid? desiredVmBefore = volume.DesiredAttachedVmId;
        Guid operationId = Guid.NewGuid();
        if (withTheWaitingId)
        {
            operationId = PendingOf(volume);
        }

        bool ended = Report(volume, method, operationId);

        Assert.False(ended);
        Assert.Equal(waiting, volume.Status);
        Assert.Equal(before, volume.Operation);
        Assert.Equal(idBefore, volume.ExternalId);
        Assert.Equal(currentVmBefore, volume.CurrentAttachedVmId);
        Assert.Equal(elsewhereBefore, volume.CurrentAttachedElsewhere);
        Assert.Equal(desiredVmBefore, volume.DesiredAttachedVmId);
    }

    // ---------- where an attach, a detach or a delete that ended goes back to ----------

    // The volume rests as Incus was last seen holding it during the wait (the report reading the volume counts):
    // missing => NotFound, otherwise Available (titan-ops features/volume/design.md#volume-reports).
    [Theory]
    [InlineData("AttachSucceeded", "", VolumeStatus.Available)]
    [InlineData("AttachSucceeded", "NotFound", VolumeStatus.NotFound)]
    [InlineData("AttachSucceeded", "NotFound,Observed", VolumeStatus.Available)]
    [InlineData("AttachFailed", "", VolumeStatus.Available)]
    [InlineData("AttachFailed", "NotFound", VolumeStatus.NotFound)]
    [InlineData("AttachFailed", "NotFound,Observed", VolumeStatus.Available)]
    [InlineData("DetachSucceeded", "", VolumeStatus.Available)]
    [InlineData("DetachSucceeded", "NotFound", VolumeStatus.NotFound)]
    [InlineData("DetachSucceeded", "NotFound,Observed", VolumeStatus.Available)]
    [InlineData("DetachFailed", "", VolumeStatus.Available)]
    [InlineData("DetachFailed", "NotFound", VolumeStatus.NotFound)]
    [InlineData("DetachFailed", "NotFound,Observed", VolumeStatus.Available)]
    [InlineData("DeleteFailed", "", VolumeStatus.Available)]
    [InlineData("DeleteFailed", "NotFound", VolumeStatus.NotFound)]
    [InlineData("DeleteFailed", "NotFound,Observed", VolumeStatus.Available)]
    public void TheEndOfAnAttachADetachOrADelete_RestsAsTheReconcilerLastSawTheVolume(
        string ending, string seenDuringTheWait, VolumeStatus expected)
    {
        Volume volume = Waiting(WaitingStatusOf(ending));
        Observe(volume, seenDuringTheWait);

        bool ended = Report(volume, ending, PendingOf(volume), sees: false);

        Assert.True(ended);
        Assert.Equal(expected, volume.Status);
        Assert.Null(volume.Operation.PendingId);
        Assert.Null(volume.Operation.LastSeenStatus);
        Assert.Equal(expected == VolumeStatus.Available, volume.OccupiesQuota);
        Assert.Equal(expected == VolumeStatus.Available, volume.IsBilled);
    }

    // A delete asked of a volume Incus lost starts from "not held"; a sighting during the wait, or a give-up that read
    // the volume, gives it back as Available.
    [Theory]
    [InlineData("", false, VolumeStatus.NotFound)]
    [InlineData("Observed", false, VolumeStatus.Available)]
    [InlineData("", true, VolumeStatus.Available)]
    public void AFailedDelete_OfAVolumeIncusLost_RestsAsTheReconcilerOrTheReportLastSawIt(
        string seenDuringTheWait, bool theReportReadTheVolume, VolumeStatus expected)
    {
        Volume volume = RestingVolume();
        volume.MarkNotFound();
        Assert.Equal(VolumeStatus.NotFound, volume.Status);
        OperationTicket delete = volume.Delete()!;
        Assert.Equal("Deleting/-", Shape(volume));
        Observe(volume, seenDuringTheWait);

        Assert.True(Report(volume, "DeleteFailed", delete.OperationId, theReportReadTheVolume));

        Assert.Equal(expected, volume.Status);
    }

    private static void Observe(Volume volume, string seen)
    {
        foreach (string observation in seen.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (observation == "NotFound")
            {
                volume.MarkNotFound();
            }
            else
            {
                volume.MarkObserved(volume.CurrentAttachedVmId, volume.CurrentAttachedElsewhere);
            }
        }
    }

    // ---------- attach and detach ----------

    [Fact]
    public void Done_OfAnAttach_RecordsWhatIncusShows_AndEndsIt()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);
        Assert.Equal("Attaching/Available", Shape(volume));

        bool ended = volume.AttachSucceeded(attach.OperationId, null, true, vm.Id, false);

        Assert.True(ended);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("Available/Available", Shape(volume));
        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.Equal((volume.ExternalId, vm.ExternalId), (attach.ExternalVolumeId, attach.ExternalVmId));
    }

    // The worker did not read the volume on Incus, so the attachment is not read either.
    [Fact]
    public void Done_OfAnAttach_ThatDidNotReadTheVolume_LeavesTheAttachmentUnread()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        volume.AttachSucceeded(attach.OperationId, null, false, vm.Id, false);

        Assert.Equal("Available/Available", Shape(volume));
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Equal(vm.Id, volume.DesiredAttachedVmId);
    }

    [Fact]
    public void Failed_OfAnAttach_PutsTheDesiredAttachmentBackToWhatIncusShows_WithTheReason()
    {
        Volume volume = RestingVolume();
        VolumeAttachProviderIds attach = volume.Attach(RunningVm());

        Failed(volume, attach.OperationId, true, null, false);

        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("Available/Available", Shape(volume));
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Equal("The volume request could not be completed.", volume.Operation.FailureReason);
    }

    [Fact]
    public void Done_OfADetach_ReleasesTheAttachment_AndEndsIt()
    {
        (Volume volume, _) = AttachedVolume();
        OperationTicket detach = volume.Detach();
        Assert.Equal("Detaching/Available", Shape(volume));
        Assert.Null(volume.DesiredAttachedVmId);

        bool ended = volume.DetachSucceeded(detach.OperationId, null, true, null, false);

        Assert.True(ended);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("Available/Available", Shape(volume));
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Null(volume.DesiredAttachedVmId);
    }

    [Fact]
    public void Failed_OfADetach_LeavesItAttached_WithTheReason()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        OperationTicket detach = volume.Detach();

        Failed(volume, detach.OperationId, true, vm.Id, false);

        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("Available/Available", Shape(volume));
        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.NotNull(volume.Operation.FailureReason);
    }

    [Fact]
    public void Done_ClearsTheReasonAnEarlierRequestLeft()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        OperationTicket failedDetach = volume.Detach();
        Failed(volume, failedDetach.OperationId, true, vm.Id, false);
        Assert.NotNull(volume.Operation.FailureReason);
        OperationTicket detach = volume.Detach();
        Assert.NotNull(volume.Operation.FailureReason);

        volume.DetachSucceeded(detach.OperationId, null, true, null, false);

        Assert.Null(volume.Operation.FailureReason);
        Assert.Null(volume.Operation.FailureDetail);
        Assert.Null(volume.Operation.FailedAt);
        Assert.Null(volume.CurrentAttachedVmId);
    }

    // An observation while an operation waits records what is seen: the status stays, the attachment is written, and
    // LastSeenStatus says whether Incus holds the volume.
    [Fact]
    public void AnObservation_ReachingWhatAnAttachAsked_LeavesTheAttachWaiting()
    {
        Volume volume = RestingVolume();
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        volume.MarkObserved(vm.Id, false);

        Assert.Equal(VolumeStatus.Attaching, volume.Status);
        Assert.Equal(attach.OperationId, PendingOf(volume));
        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
        Assert.Equal(VolumeStatus.Available, volume.Operation.LastSeenStatus);
    }

    [Fact]
    public void NotFound_WhileADeleteWaits_KeepsTheDeleteWaiting()
    {
        Volume volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;
        Assert.Equal(VolumeStatus.Available, volume.Operation.LastSeenStatus);

        volume.MarkNotFound();

        Assert.Equal(VolumeStatus.Deleting, volume.Status);
        Assert.Equal(delete.OperationId, PendingOf(volume));
        Assert.Null(volume.Operation.LastSeenStatus);
        Assert.Equal("Deleting/-", Shape(volume));
        Assert.False(volume.IsBilled);
    }

    // ---------- the delete ----------

    [Fact]
    public void Done_OfTheDelete_EndsTheVolume_AndTakesItsSnapshots()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot waitingDelete = AddRestingSnapshot(volume);
        VolumeSnapshot failedCreate = volume.WithSnapshot("never");
        volume.SnapshotCreateFailed(failedCreate.Id, PendingOf(failedCreate), null, "r", "d");
        volume.DeleteSnapshot(waitingDelete.Id);
        // A volume with a live snapshot refuses its delete; this one's snapshots no longer count.
        OperationTicket delete = volume.Delete()!;

        bool ended = volume.DeleteSucceeded(delete.OperationId, null, false, null, false);

        Assert.True(ended);
        Assert.Equal(VolumeStatus.Deleted, volume.Status);
        Assert.True(volume.IsDeleted);
        Assert.Null(volume.Operation.PendingId);
        Assert.All(volume.Snapshots, s => Assert.Equal(VolumeSnapshotStatus.Deleted, s.Status));
        Assert.All(volume.Snapshots, s => Assert.Null(s.Operation.PendingId));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheVolumeBack_WithTheReason_AndAnotherPressIsANewRequest()
    {
        Volume volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;

        Assert.True(Failed(volume, delete.OperationId));

        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("Available/Available", Shape(volume));
        Assert.NotNull(volume.Operation.FailureReason);
        Assert.True(volume.OccupiesQuota);
        Assert.NotEqual(delete.OperationId, volume.Delete()!.OperationId);
    }

    [Fact]
    public void ADeletedVolume_AbsorbsEveryReportAndObservation()
    {
        Volume volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;
        volume.DeleteSucceeded(delete.OperationId, null, false, null, false);
        VolumeOperation before = volume.Operation;

        foreach (string method in Methods)
        {
            Assert.False(Report(volume, method, delete.OperationId), method);
        }
        volume.MarkObserved(Guid.NewGuid(), true);
        volume.MarkNotFound();

        Assert.Equal(VolumeStatus.Deleted, volume.Status);
        Assert.Equal(before, volume.Operation);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.False(volume.CurrentAttachedElsewhere);
    }

    // ---------- a snapshot's own request ----------

    private static VolumeSnapshot AddRestingSnapshot(Volume volume)
    {
        VolumeSnapshot snapshot = volume.WithSnapshot($"snap-{Guid.NewGuid():N}"[..10]);
        volume.SnapshotCreateSucceeded(snapshot.Id, PendingOf(snapshot), $"vol-ext/{snapshot.Id:N}");
        return snapshot;
    }

    [Fact]
    public void ANewSnapshot_WaitsOnItsOwnCreate_IndependentOfTheVolume()
    {
        Volume volume = RestingVolume();

        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        Assert.Equal(VolumeSnapshotStatus.Creating, snapshot.Status);
        Assert.NotEqual(Guid.Empty, PendingOf(snapshot));
        Assert.True(snapshot.Operation.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Null(snapshot.Operation.LastSeenStatus);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal("Available/Available", Shape(volume));
    }

    [Fact]
    public void Done_OfASnapshotCreate_RecordsTheId_AndOnlyThatIdEndsIt()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        Guid createId = PendingOf(snapshot);

        Assert.False(volume.SnapshotCreateSucceeded(snapshot.Id, Guid.NewGuid(), "vol-ext/s1"));
        Assert.Equal("Creating/-", Shape(snapshot));
        Assert.Null(snapshot.ExternalId);
        bool ended = volume.SnapshotCreateSucceeded(snapshot.Id, createId, "vol-ext/s1");
        bool again = volume.SnapshotCreateSucceeded(snapshot.Id, createId, "vol-ext/s9");

        Assert.True(ended);
        Assert.False(again);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Equal("vol-ext/s1", snapshot.ExternalId);
        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal(1, volume.SnapshotQuotaUsage);
    }

    // Nothing would be left to delete the snapshot through: the report is refused before anything is written.
    [Fact]
    public void Done_OfASnapshotCreate_WithoutAnId_IsRefused_AndWritesNothing()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        VolumeSnapshotOperation before = snapshot.Operation;

        var thrown = Assert.Throws<BusinessException>(() => volume.SnapshotCreateSucceeded(snapshot.Id, PendingOf(snapshot), null));

        Assert.Equal("INVALID_STATUS", thrown.Code);
        Assert.Equal("Creating/-", Shape(snapshot));
        Assert.Equal(before, snapshot.Operation);
    }

    [Fact]
    public void Failed_OfASnapshotCreate_WithoutAnId_LeavesItFailed_AndOutOfTheQuota()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        Assert.True(volume.SnapshotCreateFailed(snapshot.Id, PendingOf(snapshot), null, "could not", "raw"));

        Assert.Equal("Failed/-", Shape(snapshot));
        Assert.Equal("could not", snapshot.Operation.FailureReason);
        Assert.Equal("raw", snapshot.Operation.FailureDetail);
        Assert.NotNull(snapshot.Operation.FailedAt);
        Assert.False(snapshot.CountsTowardQuota);
        Assert.False(snapshot.IsBilled);
        Assert.Equal(0, volume.SnapshotQuotaUsage);
    }

    // The give-up found the snapshot on Incus: it is kept with its id, so it can be deleted through Incus.
    [Fact]
    public void Failed_OfASnapshotCreate_CarryingAnId_KeepsTheId_AndLeavesItAvailable()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        volume.SnapshotCreateFailed(snapshot.Id, PendingOf(snapshot), "vol-ext/s1", "could not", "raw");

        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal("vol-ext/s1", snapshot.ExternalId);
        Assert.Equal("could not", snapshot.Operation.FailureReason);
        Assert.Equal(1, volume.SnapshotQuotaUsage);
        Assert.NotNull(volume.DeleteSnapshot(snapshot.Id));
        Assert.Equal(VolumeSnapshotStatus.Deleting, snapshot.Status);
    }

    // A snapshot whose create gave up before Incus held it is Failed: a late observation does not make it exist.
    [Fact]
    public void AnObservation_OfAFailedSnapshotCreate_IsIgnored()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        volume.SnapshotCreateFailed(snapshot.Id, PendingOf(snapshot), null, "could not", "raw");
        VolumeSnapshotOperation before = snapshot.Operation;

        volume.MarkSnapshotObserved(snapshot.Id);
        volume.MarkSnapshotNotFound(snapshot.Id);

        Assert.Equal("Failed/-", Shape(snapshot));
        Assert.Equal(before, snapshot.Operation);
    }

    // A snapshot that waits on its create is Creating, which always counts.
    [Fact]
    public void AnObservation_OfASnapshotBeingCreated_LeavesTheCreateWaiting()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        Guid createId = PendingOf(snapshot);

        volume.MarkSnapshotObserved(snapshot.Id);
        Assert.Equal(VolumeSnapshotStatus.Creating, snapshot.Status);
        Assert.Equal(VolumeSnapshotStatus.Available, snapshot.Operation.LastSeenStatus);
        Assert.True(snapshot.IsBilled);
        volume.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(VolumeSnapshotStatus.Creating, snapshot.Status);
        Assert.Null(snapshot.Operation.LastSeenStatus);
        Assert.False(snapshot.IsBilled);

        Assert.Equal(createId, PendingOf(snapshot));
        Assert.True(snapshot.CountsTowardQuota);
    }

    [Fact]
    public void Failed_OfASnapshotDelete_GivesItBack_WithTheReason()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;
        Assert.Equal(0, volume.SnapshotQuotaUsage);

        Assert.True(volume.SnapshotDeleteFailed(snapshot.Id, delete.OperationId, "could not", "raw"));

        Assert.Null(snapshot.Operation.PendingId);
        Assert.Equal("Available/Available", Shape(snapshot));
        Assert.Equal("could not", snapshot.Operation.FailureReason);
        Assert.Equal(1, volume.SnapshotQuotaUsage);
    }

    [Fact]
    public void Done_OfASnapshotDelete_EndsTheSnapshot_AndTheVolumeKeepsNothingWaiting()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;

        bool ended = volume.SnapshotDeleteSucceeded(snapshot.Id, delete.OperationId);

        Assert.True(ended);
        Assert.Equal(VolumeSnapshotStatus.Deleted, snapshot.Status);
        Assert.True(snapshot.IsDeleted);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Null(volume.Operation.PendingId);
        Assert.False(volume.SnapshotDeleteSucceeded(snapshot.Id, delete.OperationId));
    }

    // The snapshot rests as the reconciler last saw it during the wait: missing => NotFound, otherwise Available.
    [Theory]
    [InlineData("", VolumeSnapshotStatus.Available)]
    [InlineData("NotFound", VolumeSnapshotStatus.NotFound)]
    [InlineData("NotFound,Observed", VolumeSnapshotStatus.Available)]
    public void AFailedSnapshotDelete_RestsAsTheReconcilerLastSawTheSnapshot(string seenDuringTheWait, VolumeSnapshotStatus expected)
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;
        ObserveSnapshot(volume, snapshot, seenDuringTheWait);

        Assert.True(volume.SnapshotDeleteFailed(snapshot.Id, delete.OperationId, "could not", "raw"));

        Assert.Equal(expected, snapshot.Status);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Null(snapshot.Operation.LastSeenStatus);
        Assert.Equal(expected == VolumeSnapshotStatus.Available, snapshot.CountsTowardQuota);
        Assert.Equal(expected == VolumeSnapshotStatus.Available, snapshot.IsBilled);
    }

    // A delete asked of a snapshot Incus lost starts from "not held"; a sighting during the wait gives it back as Available.
    [Theory]
    [InlineData("", VolumeSnapshotStatus.NotFound)]
    [InlineData("Observed", VolumeSnapshotStatus.Available)]
    public void AFailedSnapshotDelete_OfASnapshotIncusLost_RestsAsTheReconcilerLastSawIt(string seenDuringTheWait, VolumeSnapshotStatus expected)
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        volume.MarkSnapshotNotFound(snapshot.Id);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;
        Assert.Null(snapshot.Operation.LastSeenStatus);
        ObserveSnapshot(volume, snapshot, seenDuringTheWait);

        Assert.True(volume.SnapshotDeleteFailed(snapshot.Id, delete.OperationId, "could not", "raw"));

        Assert.Equal(expected, snapshot.Status);
    }

    private static void ObserveSnapshot(Volume volume, VolumeSnapshot snapshot, string seen)
    {
        foreach (string observation in seen.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (observation == "NotFound")
            {
                volume.MarkSnapshotNotFound(snapshot.Id);
            }
            else
            {
                volume.MarkSnapshotObserved(snapshot.Id);
            }
        }
    }

    private static readonly string[] SnapshotMethods =
        ["CreateSucceeded", "CreateFailed", "DeleteSucceeded", "DeleteFailed"];

    private static bool SnapshotReport(Volume volume, Guid snapshotId, string method, Guid operationId) => method switch
    {
        "CreateSucceeded" => volume.SnapshotCreateSucceeded(snapshotId, operationId, "vol-ext/late"),
        "CreateFailed" => volume.SnapshotCreateFailed(snapshotId, operationId, "vol-ext/late", "late", "late"),
        "DeleteSucceeded" => volume.SnapshotDeleteSucceeded(snapshotId, operationId),
        _ => volume.SnapshotDeleteFailed(snapshotId, operationId, "late", "late"),
    };

    // Every snapshot method meets both waiting statuses with an id no operation has, and, for the methods of the other
    // operation, with the waiting operation's own id.
    public static TheoryData<string, VolumeSnapshotStatus, bool> SnapshotReportsNamingAnotherOperation()
    {
        var data = new TheoryData<string, VolumeSnapshotStatus, bool>();
        foreach (string method in SnapshotMethods)
        {
            VolumeSnapshotStatus own = VolumeSnapshotStatus.Deleting;
            if (method.StartsWith("Create"))
            {
                own = VolumeSnapshotStatus.Creating;
            }
            foreach (VolumeSnapshotStatus waiting in new[] { VolumeSnapshotStatus.Creating, VolumeSnapshotStatus.Deleting })
            {
                data.Add(method, waiting, false);
                if (own != waiting)
                {
                    data.Add(method, waiting, true);
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(SnapshotReportsNamingAnotherOperation))]
    public void ASnapshotReportNamingAnotherOperation_EndsNothing_AndChangesNothing(
        string method, VolumeSnapshotStatus waiting, bool withTheWaitingId)
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot;
        if (waiting == VolumeSnapshotStatus.Creating)
        {
            snapshot = volume.WithSnapshot("s1");
        }
        else
        {
            snapshot = AddRestingSnapshot(volume);
            volume.DeleteSnapshot(snapshot.Id);
        }
        VolumeSnapshotOperation before = snapshot.Operation;
        string? idBefore = snapshot.ExternalId;
        Guid operationId = Guid.NewGuid();
        if (withTheWaitingId)
        {
            operationId = PendingOf(snapshot);
        }

        bool ended = SnapshotReport(volume, snapshot.Id, method, operationId);

        Assert.False(ended);
        Assert.Equal(waiting, snapshot.Status);
        Assert.Equal(before, snapshot.Operation);
        Assert.Equal(idBefore, snapshot.ExternalId);
        Assert.Equal(VolumeStatus.Available, volume.Status);
    }

    // The other snapshot's operation id is not this one's.
    [Fact]
    public void ASnapshotReportNamingAnotherSnapshotsOperation_EndsNothing()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot other = AddRestingSnapshot(volume);
        VolumeSnapshot deleting = AddRestingSnapshot(volume);
        volume.DeleteSnapshot(deleting.Id);
        volume.DeleteSnapshot(other.Id);

        Assert.False(volume.SnapshotDeleteSucceeded(other.Id, PendingOf(deleting)));
        Assert.False(volume.SnapshotDeleteFailed(other.Id, PendingOf(deleting), "late", "late"));

        Assert.Equal("Deleting/Available", Shape(other));
        Assert.Equal("Deleting/Available", Shape(deleting));
        Assert.Null(other.Operation.FailureReason);
    }

    // A deleted snapshot absorbs every later report and observation.
    [Fact]
    public void ADeletedSnapshot_AbsorbsEveryReportAndObservation()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = AddRestingSnapshot(volume);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;
        volume.SnapshotDeleteSucceeded(snapshot.Id, delete.OperationId);
        VolumeSnapshotOperation before = snapshot.Operation;

        foreach (string method in SnapshotMethods)
        {
            Assert.False(SnapshotReport(volume, snapshot.Id, method, delete.OperationId), method);
        }
        volume.MarkSnapshotObserved(snapshot.Id);
        volume.MarkSnapshotNotFound(snapshot.Id);

        Assert.Equal(VolumeSnapshotStatus.Deleted, snapshot.Status);
        Assert.Equal(before, snapshot.Operation);
    }

    // A snapshot's operation is its own: the volume's report ends the volume's operation and leaves the snapshot's waiting,
    // and the snapshot's report leaves the volume's status alone.
    [Fact]
    public void TheVolumesReport_DoesNotEndTheSnapshotsRequest()
    {
        Volume volume = RestingVolume();
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        Guid createId = PendingOf(snapshot);
        Vm vm = RunningVm();
        VolumeAttachProviderIds attach = volume.Attach(vm);

        Assert.False(volume.SnapshotCreateSucceeded(snapshot.Id, attach.OperationId, "vol-ext/s1"));
        volume.AttachSucceeded(attach.OperationId, null, true, vm.Id, false);

        Assert.Equal(VolumeSnapshotStatus.Creating, snapshot.Status);
        Assert.Equal(createId, PendingOf(snapshot));
        Assert.Equal(VolumeStatus.Available, volume.Status);
        Assert.True(volume.SnapshotCreateSucceeded(snapshot.Id, createId, "vol-ext/s1"));
        Assert.Equal(VolumeStatus.Available, volume.Status);
        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
    }

    // ---------- a VM's delete is not a request of the volume ----------

    [Fact]
    public void AVmDelete_ReleasesAVolumeWithNothingWaiting_WithoutARequestOfItsOwn()
    {
        (Volume volume, Vm vm) = AttachedVolume();

        volume.ReleaseFromVmDelete(vm.Id);

        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal(VolumeStatus.Available, volume.Status);
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

        Assert.Equal(VolumeStatus.Attaching, volume.Status);
        Assert.Equal(attach.OperationId, PendingOf(volume));
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
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal(VolumeStatus.Available, volume.Status);
        volume.Delete();
        Assert.Equal(VolumeStatus.Deleting, volume.Status);
    }

    [Fact]
    public void TheVmsDeleteFailed_KeepsTheAttachment_WithNothingWaiting()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        volume.ReleaseFromVmDelete(vm.Id);

        volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);

        Assert.Equal((vm.Id, vm.Id), (volume.CurrentAttachedVmId, volume.DesiredAttachedVmId));
        Assert.Null(volume.Operation.PendingId);
    }

    [Fact]
    public void TheVmsDeleteFailed_LeavesAVolumesOwnWaitingDetach_AsItWas()
    {
        (Volume volume, Vm vm) = AttachedVolume();
        OperationTicket detach = volume.Detach();

        volume.KeepAttachmentAfterVmDeleteFailed(vm.Id);

        Assert.Equal(VolumeStatus.Detaching, volume.Status);
        Assert.Equal(detach.OperationId, PendingOf(volume));
        Assert.Null(volume.DesiredAttachedVmId);
    }
}

using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation: a request throws when it cannot be accepted,
// an observation (Mark*) never throws and ends no request, Deleted absorbs. Incus has no status word for a custom volume
// or its snapshots (titan-ops features/volume/design.md#volume-state-is-existence), so the model is existence plus, for a
// volume, the VM it is attached to: the current pair is written by observations and by a request's own report, the
// desired pair only by requests and their failure. The Seed enums name every situation so each "from every other
// situation" test walks all of them. How a request ends is VolumeRequestLifecycleTests'.
public class VolumeStateMachineTests
{
    public enum Seed
    {
        Creating, CreateFailed, Available, AttachRequested, Attached, AttachedElsewhere, DetachRequested,
        DeleteRequested, NotFound, Deleted
    }

    private static readonly Seed[] AllSeeds = Enum.GetValues<Seed>();

    private static readonly Vm AttachedVm = VmForAttach(VmShape.Running);

    private static Volume VolumeIn(Seed seed) => VolumeIn(seed, out _);

    private static Volume VolumeIn(Seed seed, out Guid createRequestId)
    {
        var v = Volume.Create(TestTenant.WithRoomToSpare().Id, "vol-1", "cluster-a", "std", 20);
        createRequestId = v.Pending!.Id;
        switch (seed)
        {
            case Seed.Creating: break; // Create() lands here
            case Seed.CreateFailed: v.MarkRequestFailed("could not create", "raw"); break;
            case Seed.Available: v.MarkCreated("vol11"); break;
            case Seed.AttachRequested: v.MarkCreated("vol11"); v.Attach(AttachedVm); break;
            case Seed.Attached: v.MarkCreated("vol11"); AttachDone(v); break;
            case Seed.AttachedElsewhere: v.MarkCreated("vol11"); v.MarkObserved(null, true); break;
            case Seed.DetachRequested: v.MarkCreated("vol11"); AttachDone(v); v.Detach(); break;
            case Seed.DeleteRequested: v.MarkCreated("vol11"); v.Delete(); break;
            case Seed.NotFound: v.MarkCreated("vol11"); v.MarkNotFound(); break;
            case Seed.Deleted: v.MarkCreated("vol11"); v.Delete(); v.MarkDeleted(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return v;
    }

    /// The attach command's Done, having seen AttachedVm use the volume.
    private static void AttachDone(Volume v)
    {
        VolumeAttachProviderIds attach = v.Attach(AttachedVm);
        v.FinishOperation(attach.OperationId, null, true, AttachedVm.Id, false);
    }

    private static VolumeOperationKind? GaveUp(Volume volume, Guid operationId, string reason,
        bool sawTheVolume = false, Guid? attachedVmId = null, bool attachedElsewhere = false) =>
        volume.FailOperation(operationId, null, sawTheVolume, attachedVmId, attachedElsewhere, reason, "raw");

    private static string Shape(Volume v)
    {
        string current = "-";
        if (v.CurrentAttachedVmId != null)
        {
            current = "vm";
        }
        if (v.CurrentAttachedElsewhere)
        {
            current += "+elsewhere";
        }
        string desired = "-";
        if (v.DesiredAttachedVmId != null)
        {
            desired = "vm";
        }
        string transition = "-";
        if (v.Transition != null)
        {
            transition = v.Transition.Value.ToString();
        }
        return $"{v.CurrentExistence}/{v.DesiredExistence}/{current}/{desired}/{transition}";
    }

    // A VM in an Incus status (seen by the reconciler), or with none: still being created, or deleted.
    public enum VmShape { Running, Ready, Stopped, Frozen, Error, Creating, Deleted }

    private static Vm VmForAttach(VmShape shape)
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        if (shape == VmShape.Creating) return vm;
        vm.MarkRunning("vm42", "10.0.0.5");
        if (shape == VmShape.Ready) vm.MarkObserved(VmState.Ready, "10.0.0.5");
        else if (shape == VmShape.Stopped) vm.MarkStopped();
        else if (shape == VmShape.Frozen) vm.MarkObserved(VmState.Frozen, "10.0.0.5");
        else if (shape == VmShape.Error) vm.MarkObserved(VmState.Error, null);
        else if (shape == VmShape.Deleted) { vm.Delete(); vm.MarkDeleted(); }
        return vm;
    }

    [Theory]
    [InlineData(Seed.Creating, "NotCreated/Exists/-/-/Creating")]
    [InlineData(Seed.CreateFailed, "NotCreated/Exists/-/-/-")]
    [InlineData(Seed.Available, "Exists/Exists/-/-/-")]
    [InlineData(Seed.AttachRequested, "Exists/Exists/-/vm/Attaching")]
    [InlineData(Seed.Attached, "Exists/Exists/vm/vm/-")]
    [InlineData(Seed.AttachedElsewhere, "Exists/Exists/-+elsewhere/-/-")]
    [InlineData(Seed.DetachRequested, "Exists/Exists/vm/-/Detaching")]
    [InlineData(Seed.DeleteRequested, "Exists/Deleted/-/-/Deleting")]
    [InlineData(Seed.NotFound, "NotFound/Exists/-/-/-")]
    [InlineData(Seed.Deleted, "Deleted/Deleted/-/-/-")]
    public void EverySituation_ReadsAsExistenceAttachmentAndTransition(Seed seed, string expected)
    {
        Assert.Equal(expected, Shape(VolumeIn(seed)));
    }

    // ---------- create ----------

    [Fact]
    public void Created_SetsTheIdAndExists_AndFulfilsTheCreateOnce()
    {
        var v = VolumeIn(Seed.Creating, out Guid createRequestId);

        Assert.NotNull(v.FinishOperation(createRequestId, "vol11", false, null, false));
        Assert.Null(v.FinishOperation(createRequestId, "vol11", false, null, false));

        Assert.Equal("vol11", v.ExternalId);
        Assert.Equal(CurrentExistence.Exists, v.CurrentExistence);
        Assert.Null(v.Transition);
    }

    [Fact]
    public void Created_Repeated_KeepsTheFirstId()
    {
        var v = VolumeIn(Seed.Creating);
        v.MarkCreated("vol11");
        v.MarkCreated("vol99");
        Assert.Equal("vol11", v.ExternalId);
    }

    // The reconciler may see the volume before the create command reports: current follows Incus, but only the
    // command's report ends the create and brings the id.
    [Fact]
    public void Observed_OnAWaitingCreate_WritesExistence_AndLeavesTheCreateWaiting()
    {
        var v = VolumeIn(Seed.Creating);

        v.MarkObserved(null, false);

        Assert.Equal(CurrentExistence.Exists, v.CurrentExistence);
        Assert.Equal(VolumeTransition.Creating, v.Transition);
        Assert.Null(v.ExternalId);
    }

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsWhy_AndEndsIt_LeavingItNotCreated()
    {
        var v = VolumeIn(Seed.Creating);
        v.MarkRequestFailed("could not create", "raw");

        Assert.Null(v.Transition);
        Assert.Equal(CurrentExistence.NotCreated, v.CurrentExistence);
        Assert.Equal("could not create", v.OperationFailureReason);
        Assert.Equal("raw", v.OperationFailureDetail);
        Assert.NotNull(v.OperationFailedAt);
    }

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.DeleteRequested)]
    public void RequestFailed_WithNoCreateWaiting_IsStale_AndRecordsNothing(Seed seed)
    {
        var v = VolumeIn(seed, out Guid createRequestId);
        string before = Shape(v);

        Assert.Null(v.FailOperation(createRequestId, null, false, null, false, "late", "late"));

        Assert.Equal(before, Shape(v));
        Assert.Null(v.OperationFailureReason);
    }

    // Not seeing the volume yet is not the create failing: the command still reports.
    [Fact]
    public void NotFound_KeepsAWaitingCreate()
    {
        var v = VolumeIn(Seed.Creating);
        v.MarkNotFound();
        Assert.Equal(VolumeTransition.Creating, v.Transition);
        Assert.Equal(CurrentExistence.NotFound, v.CurrentExistence);
    }

    // ---------- attach / detach ----------

    [Fact]
    public void Attach_WhenAvailable_RecordsTheVmAsked_AndLeavesTheCurrentAttachmentAlone()
    {
        var v = VolumeIn(Seed.Available);
        var vm = VmForAttach(VmShape.Running);

        VolumeAttachProviderIds ids = v.Attach(vm);

        Assert.Equal("vol11", ids.ExternalVolumeId);
        Assert.Equal("vm42", ids.ExternalVmId);
        Assert.Equal(vm.Id, v.DesiredAttachedVmId);
        Assert.Null(v.CurrentAttachedVmId);
        Assert.Equal(VolumeTransition.Attaching, v.Transition);
    }

    [Fact]
    public void Attach_FromEveryOtherSituation_Throws_AndMutatesNothing()
    {
        foreach (Seed seed in AllSeeds.Where(s => s != Seed.Available))
        {
            var v = VolumeIn(seed);
            string before = Shape(v);
            var ex = Assert.Throws<BusinessException>(() => v.Attach(VmForAttach(VmShape.Running)));
            Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
            Assert.Equal(before, Shape(v));
        }
    }

    [Theory]
    [InlineData(VmShape.Error)]
    [InlineData(VmShape.Creating)]
    [InlineData(VmShape.Deleted)]
    public void Attach_ToAVmIncusCannotAttachTo_Throws_AndMutatesNothing(VmShape shape)
    {
        var v = VolumeIn(Seed.Available);
        Assert.Throws<BusinessException>(() => v.Attach(VmForAttach(shape)));
        Assert.Equal("Exists/Exists/-/-/-", Shape(v));
    }

    [Fact]
    public void Attach_ToAVmInAnotherCluster_Throws()
    {
        var v = Volume.Create(TestTenant.WithRoomToSpare().Id, "vol-1", "cluster-b", "std", 20);
        v.MarkCreated("vol11");
        var ex = Assert.Throws<BusinessException>(() => v.Attach(VmForAttach(VmShape.Running)));
        Assert.Equal("VOLUME_CLUSTER_MISMATCH", ex.Code);
    }

    // No observation withdraws a waiting request: an attach not seen yet keeps waiting for its command.
    [Fact]
    public void Observed_NotYetAttached_KeepsTheAttachWaiting()
    {
        var v = VolumeIn(Seed.AttachRequested);
        v.MarkObserved(null, false);
        Assert.Equal(VolumeTransition.Attaching, v.Transition);
    }

    [Fact]
    public void Detach_FromAttached_AsksForNoVm_AndLeavesTheCurrentAttachmentAlone()
    {
        var v = VolumeIn(Seed.Attached);
        Assert.Equal("vol11", v.Detach().ProviderId);
        Assert.Null(v.DesiredAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.CurrentAttachedVmId);
        Assert.Equal(VolumeTransition.Detaching, v.Transition);
    }

    [Fact]
    public void Detach_FromEveryOtherSituation_Throws_AndMutatesNothing()
    {
        foreach (Seed seed in AllSeeds.Where(s => s != Seed.Attached))
        {
            var v = VolumeIn(seed);
            string before = Shape(v);
            var ex = Assert.Throws<BusinessException>(() => v.Detach());
            Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
            Assert.Equal(before, Shape(v));
        }
    }

    // Detaching edits every instance using the volume; Titan does not edit an instance it does not manage.
    [Fact]
    public void Detach_WhileAnInstanceOutsideTitanAlsoUsesIt_Throws()
    {
        var v = VolumeIn(Seed.Attached);
        v.MarkObserved(AttachedVm.Id, true);
        var ex = Assert.Throws<BusinessException>(() => v.Detach());
        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }

    // Seeing the volume unused is not the detach's report: only the detach command ends the detach.
    [Fact]
    public void Observed_Unattached_WritesTheCurrentAttachment_AndLeavesTheDetachWaiting()
    {
        var v = VolumeIn(Seed.DetachRequested);

        v.MarkObserved(null, false);

        Assert.Null(v.CurrentAttachedVmId);
        Assert.Equal(VolumeTransition.Detaching, v.Transition);
    }

    [Fact]
    public void Observed_StillUsedOutsideTitan_KeepsTheDetachWaiting()
    {
        var v = VolumeIn(Seed.DetachRequested);
        v.MarkObserved(null, true);
        Assert.Equal(VolumeTransition.Detaching, v.Transition);
    }

    // Attached or detached outside Titan, with nothing waiting: current follows Incus, desired stays — drift, no outcome.
    [Fact]
    public void Observed_AChangeMadeOutsideTitan_MovesOnlyTheCurrentAttachment()
    {
        var v = VolumeIn(Seed.Attached);
        v.MarkObserved(null, false);
        Assert.Null(v.CurrentAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
        Assert.Null(v.Transition);

        var other = VmForAttach(VmShape.Running);
        v.MarkObserved(other.Id, false);
        Assert.Equal(other.Id, v.CurrentAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
    }

    [Fact]
    public void AFailedAttach_GivesBackWhatIncusShows_WithTheReason()
    {
        var v = VolumeIn(Seed.Available);
        VolumeAttachProviderIds attach = v.Attach(AttachedVm);

        GaveUp(v, attach.OperationId, "attach failed", true, null, false);

        Assert.Null(v.DesiredAttachedVmId);
        Assert.Null(v.Transition);
        Assert.Equal("attach failed", v.OperationFailureReason);
        Assert.NotNull(v.Attach(VmForAttach(VmShape.Running)));
    }

    [Fact]
    public void AFailedDetach_LeavesItAttached_WithTheReason()
    {
        var v = VolumeIn(Seed.Attached);
        OperationTicket detach = v.Detach();

        GaveUp(v, detach.OperationId, "detach failed", true, AttachedVm.Id, false);

        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.CurrentAttachedVmId);
        Assert.Null(v.Transition);
        Assert.Equal("detach failed", v.OperationFailureReason);
    }

    // ---------- VM delete releases the volume (titan-ops features/volume/design.md#vm-delete-releases-volumes) ----------

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.Deleted)]
    public void KeepAttachmentAfterVmDeleteFailed_OnAVolumeNotDetachingFromThatVm_ChangesNothing(Seed seed)
    {
        var v = VolumeIn(seed);
        string before = Shape(v);

        v.KeepAttachmentAfterVmDeleteFailed(AttachedVm.Id);

        Assert.Equal(before, Shape(v));
    }

    [Fact]
    public void KeepAttachmentAfterVmDeleteFailed_ForAnotherVm_ChangesNothing()
    {
        var v = VolumeIn(Seed.Attached);
        v.ReleaseFromVmDelete(AttachedVm.Id);

        string before = Shape(v);

        v.KeepAttachmentAfterVmDeleteFailed(Guid.NewGuid());

        Assert.Equal(before, Shape(v));
    }

    [Fact]
    public void KeepAttachmentAfterVmDeleteFailed_WhileTheVolumesOwnDeleteWaits_ChangesNothing()
    {
        var v = VolumeIn(Seed.Available);
        v.Delete();
        v.MarkObserved(AttachedVm.Id, false);
        string before = Shape(v);

        v.KeepAttachmentAfterVmDeleteFailed(AttachedVm.Id);

        Assert.Equal(before, Shape(v));
    }

    // A VM's delete is not a request of the volume: it releases only an attachment asked for that VM, and never throws.
    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.DetachRequested)]
    [InlineData(Seed.Deleted)]
    public void ReleaseFromVmDelete_OnAVolumeNotAskedToBeAttachedToThatVm_ChangesNothing(Seed seed)
    {
        var v = VolumeIn(seed);
        string before = Shape(v);

        v.ReleaseFromVmDelete(AttachedVm.Id);

        Assert.Equal(before, Shape(v));
    }

    [Fact]
    public void DetachedByVmDelete_OfAnotherVm_ChangesNothing()
    {
        var v = VolumeIn(Seed.Attached);
        v.MarkDetachedByVmDelete(Guid.NewGuid());
        Assert.Equal("Exists/Exists/vm/vm/-", Shape(v));
    }

    // ---------- delete ----------

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.NotFound)]
    public void RequestDelete_WithAnId_NotAttached_IsAccepted_AndYieldsTheId(Seed seed)
    {
        var v = VolumeIn(seed);
        Assert.Equal("vol11", v.Delete()!.ProviderId);
        Assert.Equal(DesiredExistence.Deleted, v.DesiredExistence);
        Assert.Equal(VolumeTransition.Deleting, v.Transition);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.AttachedElsewhere)]
    [InlineData(Seed.DetachRequested)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    public void RequestDelete_FromEveryOtherSituation_Throws_AndMutatesNothing(Seed seed)
    {
        var v = VolumeIn(seed);
        string before = Shape(v);
        var ex = Assert.Throws<BusinessException>(() => v.Delete());
        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
        Assert.Equal(before, Shape(v));
    }

    [Fact]
    public void RequestDelete_WithALiveSnapshot_Throws_AndLeavesTheVolumeAlone()
    {
        var v = VolumeIn(Seed.Available);
        v.WithSnapshot("live").MarkCreated("vol11/s1");
        var ex = Assert.Throws<BusinessException>(() => v.Delete());
        Assert.Equal("VOLUME_HAS_SNAPSHOTS", ex.Code);
        Assert.Equal("Exists/Exists/-/-/-", Shape(v));
    }

    // A snapshot that no longer counts (delete asked for, lost on Incus, create failed) does not hold the volume
    // (titan-ops features/volume/design.md#delete-refused-with-snapshots).
    [Fact]
    public void RequestDelete_WithOnlySnapshotsThatDoNotCount_IsAccepted()
    {
        var v = VolumeIn(Seed.Available);
        var leaving = v.WithSnapshot("leaving");
        leaving.MarkCreated("vol11/s1");
        leaving.Delete();
        var lost = v.WithSnapshot("lost");
        lost.MarkCreated("vol11/s2");
        lost.MarkNotFound();
        v.WithSnapshot("failed").MarkRequestFailed("could not create", "raw");

        Assert.Equal("vol11", v.Delete()!.ProviderId);
        Assert.Equal(VolumeTransition.Deleting, v.Transition);
    }

    // The delete command's Done.
    [Fact]
    public void MarkDeleted_FulfilsTheDeleteOnce()
    {
        var v = VolumeIn(Seed.Available);
        OperationTicket delete = v.Delete()!;

        Assert.NotNull(v.FinishOperation(delete.OperationId, null, false, null, false));
        Assert.Null(v.FinishOperation(delete.OperationId, null, false, null, false));

        Assert.Equal(CurrentExistence.Deleted, v.CurrentExistence);
        Assert.True(v.IsDeleted);
    }

    // Only the delete command ends a delete: the reconciler finding the volume gone is NotFound.
    [Fact]
    public void NotFound_KeepsAWaitingDelete()
    {
        var v = VolumeIn(Seed.DeleteRequested);
        v.MarkNotFound();
        Assert.Equal(CurrentExistence.NotFound, v.CurrentExistence);
        Assert.Equal(VolumeTransition.Deleting, v.Transition);
    }

    [Fact]
    public void AFailedDelete_GivesTheVolumeBack_WithTheReason_AndAnotherPressIsANewRequest()
    {
        var v = VolumeIn(Seed.Available);
        OperationTicket first = v.Delete()!;

        GaveUp(v, first.OperationId, "delete failed");

        Assert.Equal("Exists/Exists/-/-/-", Shape(v));
        Assert.Equal("delete failed", v.OperationFailureReason);

        OperationTicket second = v.Delete()!;
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    // A give-up names its request; one the volume is not waiting on (another request's, or none) is ignored.
    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Available)]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.Deleted)]
    public void AGiveUp_ForARequestNotWaiting_ChangesNothing_AndReturnsNull(Seed seed)
    {
        var v = VolumeIn(seed);
        string before = Shape(v);

        Assert.Null(GaveUp(v, Guid.NewGuid(), "late"));

        Assert.Equal(before, Shape(v));
        Assert.Null(v.OperationFailureReason);
    }

    [Fact]
    public void RequestDelete_OfACreateThatFailedBeforeIncusHadIt_DeletesAtOnce_WithNoTicket()
    {
        var v = VolumeIn(Seed.CreateFailed);
        Assert.Null(v.Delete());
        Assert.Equal("Deleted/Deleted/-/-/-", Shape(v));
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Available)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void RequestDelete_OfAVolumeThatMayBeOnIncus_DoesNotDeleteHere(Seed seed)
    {
        var v = VolumeIn(seed);
        Assert.False(LocalDelete.DeletedHere(() => v.Delete()));
    }

    // ---------- snapshot precondition ----------

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.AttachedElsewhere)]
    public void AddSnapshot_WhenIncusHasItAndNothingWaits_YieldsTheId(Seed seed)
    {
        Assert.Equal("vol11", VolumeIn(seed).AddSnapshot(Guid.NewGuid(), "s").ProviderId);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.DetachRequested)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void AddSnapshot_FromEveryOtherSituation_Throws(Seed seed)
    {
        var ex = Assert.Throws<BusinessException>(() => VolumeIn(seed).AddSnapshot(Guid.NewGuid(), "s"));
        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }

    // ---------- Deleted absorbs ----------

    [Fact]
    public void Deleted_IgnoresEveryObservation_AndNeverThrows()
    {
        var v = VolumeIn(Seed.Deleted, out Guid createRequestId);
        Assert.Null(v.FinishOperation(createRequestId, "vol99", false, null, false));
        v.MarkObserved(AttachedVm.Id, true);
        v.MarkNotFound();
        Assert.Null(GaveUp(v, createRequestId, "late"));
        v.MarkDetachedByVmDelete(AttachedVm.Id);
        v.ReleaseFromVmDelete(AttachedVm.Id);
        Assert.Equal("Deleted/Deleted/-/-/-", Shape(v));
        Assert.Equal("vol11", v.ExternalId);
        Assert.Null(v.OperationFailureReason);
    }

    [Fact]
    public void NotFound_ThenSeenAgain_ExistsAgain()
    {
        var v = VolumeIn(Seed.NotFound);
        v.MarkObserved(null, false);
        Assert.Equal(CurrentExistence.Exists, v.CurrentExistence);
    }

    // ================= volume snapshot =================

    public enum SnapSeed { Creating, CreateFailed, Exists, DeleteRequested, NotFound, Deleted }

    private static readonly SnapSeed[] AllSnapSeeds = Enum.GetValues<SnapSeed>();

    private static VolumeSnapshot SnapshotIn(SnapSeed seed) => SnapshotIn(seed, out _);

    private static VolumeSnapshot SnapshotIn(SnapSeed seed, out Guid createRequestId)
    {
        var s = VolumeIn(Seed.Available).WithSnapshot("snap-1");
        createRequestId = s.Pending!.Id;
        switch (seed)
        {
            case SnapSeed.Creating: break;
            case SnapSeed.CreateFailed: s.MarkRequestFailed("could not create", "raw"); break;
            case SnapSeed.Exists: s.MarkCreated("vol11/s5"); break;
            case SnapSeed.DeleteRequested: s.MarkCreated("vol11/s5"); s.Delete(); break;
            case SnapSeed.NotFound: s.MarkCreated("vol11/s5"); s.MarkNotFound(); break;
            case SnapSeed.Deleted:
                s.MarkCreated("vol11/s5");
                s.FinishOperation(s.Delete()!.OperationId, null);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return s;
    }

    private static string SnapShape(VolumeSnapshot s)
    {
        string transition = "-";
        if (s.Transition != null)
        {
            transition = s.Transition.Value.ToString();
        }
        return $"{s.CurrentExistence}/{s.DesiredExistence}/{transition}";
    }

    [Theory]
    [InlineData(SnapSeed.Creating, "NotCreated/Exists/Creating")]
    [InlineData(SnapSeed.CreateFailed, "NotCreated/Exists/-")]
    [InlineData(SnapSeed.Exists, "Exists/Exists/-")]
    [InlineData(SnapSeed.DeleteRequested, "Exists/Deleted/Deleting")]
    [InlineData(SnapSeed.NotFound, "NotFound/Exists/-")]
    [InlineData(SnapSeed.Deleted, "Deleted/Deleted/-")]
    public void Snapshot_EverySituation_ReadsAsExistenceAndTransition(SnapSeed seed, string expected)
    {
        Assert.Equal(expected, SnapShape(SnapshotIn(seed)));
    }

    [Fact]
    public void Snapshot_Created_FulfilsTheCreateOnce()
    {
        var s = SnapshotIn(SnapSeed.Creating, out Guid createRequestId);
        Assert.NotNull(s.FinishOperation(createRequestId, "vol11/s5"));
        Assert.Null(s.FinishOperation(createRequestId, "vol11/s5"));
        s.MarkObserved();
        Assert.Equal("Exists/Exists/-", SnapShape(s));
    }

    [Fact]
    public void Snapshot_RequestFailed_RecordsWhy_OnlyWhileACreateWaits()
    {
        var s = SnapshotIn(SnapSeed.CreateFailed);
        Assert.Equal("could not create", s.OperationFailureReason);
        Assert.Equal("raw", s.OperationFailureDetail);

        var exists = SnapshotIn(SnapSeed.Exists, out Guid createRequestId);
        Assert.Null(exists.FailOperation(createRequestId, null, "late", "late"));
        Assert.Null(exists.OperationFailureReason);
    }

    [Theory]
    [InlineData(SnapSeed.Exists)]
    [InlineData(SnapSeed.NotFound)]
    public void Snapshot_RequestDelete_WithAnId_IsAccepted(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        Assert.Equal("vol11/s5", s.Delete()!.ProviderId);
        Assert.Equal(VolumeSnapshotTransition.Deleting, s.Transition);
    }

    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.DeleteRequested)]
    [InlineData(SnapSeed.Deleted)]
    public void Snapshot_RequestDelete_FromEveryOtherSituation_Throws_AndMutatesNothing(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        string before = SnapShape(s);
        var ex = Assert.Throws<BusinessException>(() => s.Delete());
        Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", ex.Code);
        Assert.Equal(before, SnapShape(s));
    }

    [Fact]
    public void Snapshot_Restore_OnlyWhenIncusHasItAndNothingWaits()
    {
        Assert.Equal("vol11/s5", SnapshotIn(SnapSeed.Exists).ProviderIdForRestore());
        foreach (SnapSeed seed in AllSnapSeeds.Where(s => s != SnapSeed.Exists))
        {
            var ex = Assert.Throws<BusinessException>(() => SnapshotIn(seed).ProviderIdForRestore());
            Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", ex.Code);
        }
    }

    [Fact]
    public void Snapshot_NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var s = SnapshotIn(SnapSeed.Exists);
        OperationTicket delete = s.Delete()!;
        s.MarkNotFound();
        Assert.Equal(VolumeSnapshotTransition.Deleting, s.Transition);
        Assert.NotNull(s.FinishOperation(delete.OperationId, null));
        Assert.Null(s.FinishOperation(delete.OperationId, null));
        Assert.True(s.IsDeleted);
    }

    [Fact]
    public void Snapshot_AFailedDelete_GivesItBack_WithTheReason()
    {
        var s = SnapshotIn(SnapSeed.Exists);
        OperationTicket delete = s.Delete()!;
        s.FailOperation(delete.OperationId, null, "delete failed", "raw");
        Assert.Equal("Exists/Exists/-", SnapShape(s));
        Assert.Equal("delete failed", s.OperationFailureReason);
        Assert.Equal("vol11/s5", s.Delete()!.ProviderId);
    }

    // A give-up names its request; one the snapshot is not waiting on (another request's, or none) is ignored.
    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.Exists)]
    [InlineData(SnapSeed.Deleted)]
    public void Snapshot_AGiveUp_ForARequestNotWaiting_ChangesNothing_AndReturnsNull(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        string before = SnapShape(s);

        Assert.Null(s.FailOperation(Guid.NewGuid(), null, "late", "late"));

        Assert.Equal(before, SnapShape(s));
        Assert.Null(s.OperationFailureReason);
    }

    [Fact]
    public void Snapshot_RequestDelete_DeletesHere_OnlyForACreateThatFailedBeforeIncusHadIt()
    {
        var failed = SnapshotIn(SnapSeed.CreateFailed);
        Assert.Null(failed.Delete());
        Assert.Equal("Deleted/Deleted/-", SnapShape(failed));

        foreach (SnapSeed seed in new[] { SnapSeed.Creating, SnapSeed.Exists, SnapSeed.NotFound, SnapSeed.Deleted })
        {
            VolumeSnapshot snapshot = SnapshotIn(seed);
            Assert.False(LocalDelete.DeletedHere(() => snapshot.Delete()));
        }
    }

    [Fact]
    public void Snapshot_Deleted_IgnoresEveryObservation_AndNeverThrows()
    {
        var s = SnapshotIn(SnapSeed.Deleted, out Guid createRequestId);
        Assert.Null(s.FinishOperation(createRequestId, "vol11/s9"));
        s.MarkObserved();
        s.MarkNotFound();
        Assert.Null(s.FailOperation(createRequestId, null, "late", "late"));
        s.MarkDeleted();
        Assert.Equal("Deleted/Deleted/-", SnapShape(s));
        Assert.Equal("vol11/s5", s.ExternalId);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.DeleteRequested)]
    public void Snapshot_Create_OfAVolumeNotResting_Throws(Seed seed)
    {
        var ex = Assert.Throws<BusinessException>(
            () => VolumeIn(seed).AddSnapshot(Guid.NewGuid(), "snap-1"));
        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }
}

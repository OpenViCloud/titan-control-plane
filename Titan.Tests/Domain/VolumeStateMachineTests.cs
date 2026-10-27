using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-versus-observation: a request throws when it cannot be accepted,
// an observation (Mark*) never throws, Deleted absorbs. Incus has no status word for a custom volume or its snapshots
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#volume-state-is-existence), so the model is existence plus, for a volume, the VM it
// is attached to: the current pair is written only by observations, the desired pair only by requests. The Seed enums
// name every situation so each "from every other situation" test walks all of them.
public class VolumeStateMachineTests
{
    public enum Seed
    {
        Creating, CreateFailed, Available, AttachRequested, Attached, AttachedElsewhere, DetachRequested,
        DeleteRequested, NotFound, Deleted
    }

    private static readonly Seed[] AllSeeds = Enum.GetValues<Seed>();

    private static readonly Vm AttachedVm = VmForAttach(VmShape.Running);

    private static Volume VolumeIn(Seed seed)
    {
        var v = Volume.CreateUnchecked(Guid.NewGuid(), "vol-1", "zone-a", "std", 20);
        switch (seed)
        {
            case Seed.Creating: break; // Create() lands here
            case Seed.CreateFailed: v.MarkRequestFailed("could not create", "raw"); break;
            case Seed.Available: v.MarkCreated("vol11"); break;
            case Seed.AttachRequested: v.MarkCreated("vol11"); v.Attach(AttachedVm); break;
            case Seed.Attached: v.MarkCreated("vol11"); v.Attach(AttachedVm); v.MarkObserved(AttachedVm.Id, false); break;
            case Seed.AttachedElsewhere: v.MarkCreated("vol11"); v.MarkObserved(null, true); break;
            case Seed.DetachRequested:
                v.MarkCreated("vol11"); v.Attach(AttachedVm); v.MarkObserved(AttachedVm.Id, false); v.Detach(); break;
            case Seed.DeleteRequested: v.MarkCreated("vol11"); v.RequestDelete(liveSnapshotCount: 0); break;
            case Seed.NotFound: v.MarkCreated("vol11"); v.MarkNotFound(); break;
            case Seed.Deleted: v.MarkCreated("vol11"); v.RequestDelete(liveSnapshotCount: 0); v.MarkDeleted(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return v;
    }

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
        var vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl-1", "zone-a", 20);
        if (shape == VmShape.Creating) return vm;
        vm.MarkRunning("vm42", "10.0.0.5");
        if (shape == VmShape.Ready) vm.MarkObserved(VmState.Ready, "10.0.0.5");
        else if (shape == VmShape.Stopped) vm.MarkStopped();
        else if (shape == VmShape.Frozen) vm.MarkObserved(VmState.Frozen, "10.0.0.5");
        else if (shape == VmShape.Error) vm.MarkObserved(VmState.Error, null);
        else if (shape == VmShape.Deleted) { vm.RequestDelete(); vm.MarkDeleted(); }
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
        var v = VolumeIn(Seed.Creating);

        Assert.True(v.MarkCreated("vol11"));
        Assert.False(v.MarkCreated("vol11"));

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

    // The Created report was lost, but the reconciler sees the volume: that ends the create.
    [Fact]
    public void Observed_OnAWaitingCreate_FulfilsIt_AsCreating()
    {
        var v = VolumeIn(Seed.Creating);
        Assert.Equal(VolumeTransition.Creating, v.MarkObserved(null, false));
        Assert.Null(v.Transition);
        Assert.Equal(CurrentExistence.Exists, v.CurrentExistence);
    }

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsWhy_AndEndsIt_LeavingItNotCreated()
    {
        var v = VolumeIn(Seed.Creating);
        v.MarkRequestFailed("could not create", "raw");

        Assert.Null(v.Transition);
        Assert.Equal(CurrentExistence.NotCreated, v.CurrentExistence);
        Assert.Equal("could not create", v.RequestFailureReason);
        Assert.Equal("raw", v.RequestFailureDetail);
        Assert.NotNull(v.RequestFailedAt);
    }

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.DeleteRequested)]
    public void RequestFailed_WithNoCreateWaiting_IsStale_AndRecordsNothing(Seed seed)
    {
        var v = VolumeIn(seed);
        string before = Shape(v);
        v.MarkRequestFailed("late", "late");
        Assert.Equal(before, Shape(v));
        Assert.Null(v.RequestFailureReason);
    }

    [Fact]
    public void NotFound_EndsAWaitingCreate()
    {
        var v = VolumeIn(Seed.Creating);
        v.MarkNotFound();
        Assert.Null(v.Transition);
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
    public void Attach_ToAVmInAnotherZone_Throws()
    {
        var v = Volume.CreateUnchecked(Guid.NewGuid(), "vol-1", "zone-b", "std", 20);
        v.MarkCreated("vol11");
        var ex = Assert.Throws<BusinessException>(() => v.Attach(VmForAttach(VmShape.Running)));
        Assert.Equal("VOLUME_CLUSTER_MISMATCH", ex.Code);
    }

    [Fact]
    public void Observed_AttachedToTheVmAsked_FulfilsTheAttachOnce()
    {
        var v = VolumeIn(Seed.AttachRequested);

        Assert.Equal(VolumeTransition.Attaching, v.MarkObserved(AttachedVm.Id, false));
        Assert.Null(v.MarkObserved(AttachedVm.Id, false));

        Assert.Equal(AttachedVm.Id, v.CurrentAttachedVmId);
        Assert.Null(v.Transition);
    }

    // No observation withdraws a waiting request: an attach not seen yet keeps waiting for its command.
    [Fact]
    public void Observed_NotYetAttached_KeepsTheAttachWaiting()
    {
        var v = VolumeIn(Seed.AttachRequested);
        Assert.Null(v.MarkObserved(null, false));
        Assert.Equal(VolumeTransition.Attaching, v.Transition);
    }

    [Fact]
    public void Detach_FromAttached_AsksForNoVm_AndLeavesTheCurrentAttachmentAlone()
    {
        var v = VolumeIn(Seed.Attached);
        Assert.Equal("vol11", v.Detach());
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

    [Fact]
    public void Observed_Unattached_FulfilsTheDetachOnce()
    {
        var v = VolumeIn(Seed.DetachRequested);

        Assert.Equal(VolumeTransition.Detaching, v.MarkObserved(null, false));
        Assert.Null(v.MarkObserved(null, false));

        Assert.Null(v.CurrentAttachedVmId);
        Assert.Null(v.Transition);
    }

    [Fact]
    public void Observed_StillUsedOutsideTitan_KeepsTheDetachWaiting()
    {
        var v = VolumeIn(Seed.DetachRequested);
        Assert.Null(v.MarkObserved(null, true));
        Assert.Equal(VolumeTransition.Detaching, v.Transition);
    }

    // Attached or detached outside Titan, with nothing waiting: current follows Incus, desired stays — drift, no outcome.
    [Fact]
    public void Observed_AChangeMadeOutsideTitan_MovesOnlyTheCurrentAttachment()
    {
        var v = VolumeIn(Seed.Attached);
        Assert.Null(v.MarkObserved(null, false));
        Assert.Null(v.CurrentAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
        Assert.Null(v.Transition);

        var other = VmForAttach(VmShape.Running);
        Assert.Null(v.MarkObserved(other.Id, false));
        Assert.Equal(other.Id, v.CurrentAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
    }

    [Fact]
    public void AFailedAttach_GivesBackWhatIncusShows_WithTheReason()
    {
        var v = VolumeIn(Seed.AttachRequested);
        v.AbandonRequest("attach failed", "raw");

        Assert.Null(v.DesiredAttachedVmId);
        Assert.Null(v.Transition);
        Assert.Equal("attach failed", v.RequestFailureReason);
        Assert.NotNull(v.Attach(VmForAttach(VmShape.Running)));
    }

    [Fact]
    public void AFailedDetach_LeavesItAttached_WithTheReason()
    {
        var v = VolumeIn(Seed.DetachRequested);
        v.AbandonRequest("detach failed", "raw");

        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.CurrentAttachedVmId);
        Assert.Null(v.Transition);
        Assert.Equal("detach failed", v.RequestFailureReason);
    }

    // ---------- VM delete releases the volume (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#vm-delete-releases-volumes) ----------

    [Theory]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.AttachRequested)]
    public void DetachWithDeletedVm_ThenTheVmDeleted_LeavesTheVolumeAvailable_SoItCanBeDeleted(Seed seed)
    {
        var v = VolumeIn(seed);
        v.DetachWithDeletedVm();
        Assert.Equal(VolumeTransition.Detaching, v.Transition);

        v.MarkDetachedByVmDelete(AttachedVm.Id);

        Assert.Equal("Exists/Exists/-/-/-", Shape(v));
        Assert.Equal("vol11", v.RequestDelete(liveSnapshotCount: 0));
    }

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.DetachRequested)]
    [InlineData(Seed.Deleted)]
    public void DetachWithDeletedVm_OnAVolumeNotAskedToBeAttached_Throws(Seed seed)
    {
        var v = VolumeIn(seed);
        Assert.Throws<BusinessException>(() => v.DetachWithDeletedVm());
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
        Assert.Equal("vol11", v.RequestDelete(liveSnapshotCount: 0));
        Assert.Equal(DesiredExistence.Deleted, v.DesiredExistence);
        Assert.Equal(VolumeTransition.Deleting, v.Transition);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
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
        var ex = Assert.Throws<BusinessException>(() => v.RequestDelete(liveSnapshotCount: 0));
        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
        Assert.Equal(before, Shape(v));
    }

    [Fact]
    public void RequestDelete_WithALiveSnapshot_Throws_AndLeavesTheVolumeAlone()
    {
        var v = VolumeIn(Seed.Available);
        var ex = Assert.Throws<BusinessException>(() => v.RequestDelete(liveSnapshotCount: 1));
        Assert.Equal("VOLUME_HAS_SNAPSHOTS", ex.Code);
        Assert.Equal("Exists/Exists/-/-/-", Shape(v));
    }

    [Fact]
    public void MarkDeleted_FulfilsTheDeleteOnce()
    {
        var v = VolumeIn(Seed.DeleteRequested);
        Assert.True(v.MarkDeleted());
        Assert.False(v.MarkDeleted());
        Assert.Equal(CurrentExistence.Deleted, v.CurrentExistence);
        Assert.True(v.IsTerminal);
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
        var v = VolumeIn(Seed.DeleteRequested);
        Guid? firstIntent = v.PendingIntentId;
        v.AbandonRequest("delete failed", "raw");

        Assert.Equal("Exists/Exists/-/-/-", Shape(v));
        Assert.Equal("delete failed", v.RequestFailureReason);

        v.RequestDelete(liveSnapshotCount: 0);
        Assert.NotEqual(firstIntent, v.PendingIntentId);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Available)]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.Deleted)]
    public void AbandonRequest_WithNoAttachDetachOrDeleteWaiting_Throws(Seed seed)
    {
        var v = VolumeIn(seed);
        Assert.Throws<BusinessException>(() => v.AbandonRequest("x", "y"));
    }

    [Fact]
    public void DeleteLocally_OfACreateThatFailedBeforeIncusHadIt_DeletesAtOnce()
    {
        var v = VolumeIn(Seed.CreateFailed);
        v.DeleteLocally();
        Assert.Equal("Deleted/Deleted/-/-/-", Shape(v));
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Available)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void DeleteLocally_OfAVolumeThatMayBeOnIncus_Throws(Seed seed)
    {
        var v = VolumeIn(seed);
        Assert.Throws<BusinessException>(() => v.DeleteLocally());
    }

    // ---------- snapshot precondition ----------

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.AttachedElsewhere)]
    public void BeginSnapshot_WhenIncusHasItAndNothingWaits_YieldsTheId(Seed seed)
    {
        Assert.Equal("vol11", VolumeIn(seed).BeginSnapshot());
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.DetachRequested)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.Deleted)]
    public void BeginSnapshot_FromEveryOtherSituation_Throws(Seed seed)
    {
        var ex = Assert.Throws<BusinessException>(() => VolumeIn(seed).BeginSnapshot());
        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }

    // ---------- Deleted absorbs ----------

    [Fact]
    public void Deleted_IgnoresEveryObservation_AndNeverThrows()
    {
        var v = VolumeIn(Seed.Deleted);
        Assert.False(v.MarkCreated("vol99"));
        Assert.Null(v.MarkObserved(AttachedVm.Id, true));
        v.MarkNotFound();
        v.MarkRequestFailed("late", "late");
        v.MarkDetachedByVmDelete(AttachedVm.Id);
        Assert.False(v.MarkDeleted());
        Assert.Equal("Deleted/Deleted/-/-/-", Shape(v));
        Assert.Equal("vol11", v.ExternalId);
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

    private static VolumeSnapshot SnapshotIn(SnapSeed seed)
    {
        var s = VolumeSnapshot.Create(VolumeIn(Seed.Available), TestTenant.WithRoomToSpare(), 0, "snap-1");
        switch (seed)
        {
            case SnapSeed.Creating: break;
            case SnapSeed.CreateFailed: s.MarkRequestFailed("could not create", "raw"); break;
            case SnapSeed.Exists: s.MarkCreated("vol11/s5"); break;
            case SnapSeed.DeleteRequested: s.MarkCreated("vol11/s5"); s.RequestDelete(); break;
            case SnapSeed.NotFound: s.MarkCreated("vol11/s5"); s.MarkNotFound(); break;
            case SnapSeed.Deleted: s.MarkCreated("vol11/s5"); s.RequestDelete(); s.MarkDeleted(); break;
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
        var s = SnapshotIn(SnapSeed.Creating);
        Assert.True(s.MarkCreated("vol11/s5"));
        Assert.False(s.MarkCreated("vol11/s5"));
        Assert.False(s.MarkObserved());
        Assert.Equal("Exists/Exists/-", SnapShape(s));
    }

    [Fact]
    public void Snapshot_Observed_OnAWaitingCreate_FulfilsIt()
    {
        var s = SnapshotIn(SnapSeed.Creating);
        Assert.True(s.MarkObserved());
        Assert.Equal("Exists/Exists/-", SnapShape(s));
    }

    [Fact]
    public void Snapshot_RequestFailed_RecordsWhy_OnlyWhileACreateWaits()
    {
        var s = SnapshotIn(SnapSeed.CreateFailed);
        Assert.Equal("could not create", s.RequestFailureReason);
        Assert.Equal("raw", s.RequestFailureDetail);

        var exists = SnapshotIn(SnapSeed.Exists);
        exists.MarkRequestFailed("late", "late");
        Assert.Null(exists.RequestFailureReason);
    }

    [Theory]
    [InlineData(SnapSeed.Exists)]
    [InlineData(SnapSeed.NotFound)]
    public void Snapshot_RequestDelete_WithAnId_IsAccepted(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        Assert.Equal("vol11/s5", s.RequestDelete());
        Assert.Equal(VolumeSnapshotTransition.Deleting, s.Transition);
    }

    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.CreateFailed)]
    [InlineData(SnapSeed.DeleteRequested)]
    [InlineData(SnapSeed.Deleted)]
    public void Snapshot_RequestDelete_FromEveryOtherSituation_Throws_AndMutatesNothing(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        string before = SnapShape(s);
        var ex = Assert.Throws<BusinessException>(() => s.RequestDelete());
        Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", ex.Code);
        Assert.Equal(before, SnapShape(s));
    }

    [Fact]
    public void Snapshot_Restore_OnlyWhenIncusHasItAndNothingWaits()
    {
        Assert.Equal("vol11/s5", SnapshotIn(SnapSeed.Exists).BeginRestore());
        foreach (SnapSeed seed in AllSnapSeeds.Where(s => s != SnapSeed.Exists))
        {
            var ex = Assert.Throws<BusinessException>(() => SnapshotIn(seed).BeginRestore());
            Assert.Equal("VOLUME_SNAPSHOT_INVALID_STATE", ex.Code);
        }
    }

    [Fact]
    public void Snapshot_NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var s = SnapshotIn(SnapSeed.DeleteRequested);
        s.MarkNotFound();
        Assert.Equal(VolumeSnapshotTransition.Deleting, s.Transition);
        Assert.True(s.MarkDeleted());
        Assert.False(s.MarkDeleted());
        Assert.True(s.IsTerminal);
    }

    [Fact]
    public void Snapshot_AFailedDelete_GivesItBack_WithTheReason()
    {
        var s = SnapshotIn(SnapSeed.DeleteRequested);
        s.AbandonRequest("delete failed", "raw");
        Assert.Equal("Exists/Exists/-", SnapShape(s));
        Assert.Equal("delete failed", s.RequestFailureReason);
        Assert.Equal("vol11/s5", s.RequestDelete());
    }

    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.Exists)]
    [InlineData(SnapSeed.Deleted)]
    public void Snapshot_AbandonRequest_WithNoDeleteWaiting_Throws(SnapSeed seed)
    {
        Assert.Throws<BusinessException>(() => SnapshotIn(seed).AbandonRequest("x", "y"));
    }

    [Fact]
    public void Snapshot_DeleteLocally_OnlyForACreateThatFailedBeforeIncusHadIt()
    {
        var failed = SnapshotIn(SnapSeed.CreateFailed);
        failed.DeleteLocally();
        Assert.Equal("Deleted/Deleted/-", SnapShape(failed));

        foreach (SnapSeed seed in new[] { SnapSeed.Creating, SnapSeed.Exists, SnapSeed.NotFound, SnapSeed.Deleted })
        {
            Assert.Throws<BusinessException>(() => SnapshotIn(seed).DeleteLocally());
        }
    }

    [Fact]
    public void Snapshot_Deleted_IgnoresEveryObservation_AndNeverThrows()
    {
        var s = SnapshotIn(SnapSeed.Deleted);
        Assert.False(s.MarkCreated("vol11/s9"));
        Assert.False(s.MarkObserved());
        s.MarkNotFound();
        s.MarkRequestFailed("late", "late");
        Assert.False(s.MarkDeleted());
        Assert.Equal("Deleted/Deleted/-", SnapShape(s));
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.NotFound)]
    [InlineData(Seed.DeleteRequested)]
    public void Snapshot_Create_OfAVolumeNotResting_Throws(Seed seed)
    {
        var ex = Assert.Throws<BusinessException>(
            () => VolumeSnapshot.Create(VolumeIn(seed), TestTenant.WithRoomToSpare(), 0, "snap-1"));
        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
    }
}

using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation: a request throws when it cannot be accepted,
// an observation (Mark*) never throws and ends no operation, Deleted absorbs. Incus has no status word for a custom volume
// or its snapshots (titan-ops features/volume/design.md#volume-state-is-existence), so the one status says what waits or
// whether Incus holds it at rest; while an operation waits, the volume keeps whether Incus was last seen holding it
// (InstanceStatus). The attachment is three fields next to the status: the VM Incus reports using the volume, whether an
// outside instance does, and the VM the customer asked for. Observations and an operation's own report write the
// current attachment, only requests and give-ups write the asked one. The Seed enums name every situation so each "from
// every other situation" test walks all of them. How an operation ends is VolumeRequestLifecycleTests'.
public class VolumeStateMachineTests
{
    public enum Seed
    {
        Creating, CreateFailed, Available, AttachRequested, Attached, AttachedElsewhere, DetachRequested,
        DeleteRequested, NotFound, Deleted
    }

    private static readonly Seed[] AllSeeds = Enum.GetValues<Seed>();

    // The four statuses an operation waits in; each has its own Succeeded and Failed method.
    private static readonly VolumeStatus[] Waiting =
        [VolumeStatus.Creating, VolumeStatus.Attaching, VolumeStatus.Detaching, VolumeStatus.Deleting];

    private static readonly Vm AttachedVm = VmForAttach(VmShape.Running);

    private static Volume VolumeIn(Seed seed) => VolumeIn(seed, out _);

    private static Volume VolumeIn(Seed seed, out Guid createRequestId)
    {
        var v = Volume.Create(TestTenant.WithRoomToSpare().Id, "vol-1", "cluster-a", "std", 20);
        createRequestId = v.Operation.PendingId!.Value;
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
        v.AttachSucceeded(attach.OperationId, null, true, AttachedVm.Id, false);
    }

    private static Guid PendingOf(Volume v) => v.Operation.PendingId!.Value;

    /// The worker's Done through the method of the operation named by `method`; false when it is ignored.
    private static bool Succeeded(Volume volume, VolumeStatus method, Guid operationId, string? externalId = null,
        bool sawTheVolume = false, Guid? attachedVmId = null, bool attachedElsewhere = false)
    {
        switch (method)
        {
            case VolumeStatus.Creating:
                return volume.CreateSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
            case VolumeStatus.Attaching:
                return volume.AttachSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
            case VolumeStatus.Detaching:
                return volume.DetachSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
            case VolumeStatus.Deleting:
                return volume.DeleteSucceeded(operationId, externalId, sawTheVolume, attachedVmId, attachedElsewhere);
            default:
                throw new ArgumentOutOfRangeException(nameof(method));
        }
    }

    /// The worker's give-up through the method of the operation named by `method`; false when it is ignored.
    private static bool GaveUp(Volume volume, VolumeStatus method, Guid operationId, string reason,
        bool sawTheVolume = false, Guid? attachedVmId = null, bool attachedElsewhere = false, string? externalId = null)
    {
        switch (method)
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
                throw new ArgumentOutOfRangeException(nameof(method));
        }
    }

    /// The give-up of the operation the volume waits in, which its status says.
    private static bool GaveUp(Volume volume, Guid operationId, string reason,
        bool sawTheVolume = false, Guid? attachedVmId = null, bool attachedElsewhere = false) =>
        GaveUp(volume, volume.Status, operationId, reason, sawTheVolume, attachedVmId, attachedElsewhere);

    // The status, then whether Incus holds the volume as far as Titan last knew, then the current attachment, then the
    // VM asked for.
    private static string Shape(Volume v)
    {
        string held = "-";
        if (v.InstanceStatus != null)
        {
            held = v.InstanceStatus.Value.ToString();
        }
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
        return $"{v.Status}/{held}/{current}/{desired}";
    }

    // A VM in an Incus status (seen by the reconciler), or with none: still being created, or deleted.
    public enum VmShape { Running, Ready, Stopped, Frozen, Error, Creating, Deleted }

    private static Vm VmForAttach(VmShape shape)
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        if (shape == VmShape.Creating) return vm;
        vm.MarkRunning("vm42", "10.0.0.5");
        if (shape == VmShape.Ready) vm.MarkObserved(VmStatus.Ready, "10.0.0.5");
        else if (shape == VmShape.Stopped) vm.MarkStopped();
        else if (shape == VmShape.Frozen) vm.MarkObserved(VmStatus.Frozen, "10.0.0.5");
        else if (shape == VmShape.Error) vm.MarkObserved(VmStatus.Error, null);
        else if (shape == VmShape.Deleted) { vm.Delete(Guid.NewGuid()); vm.MarkDeleted(); }
        return vm;
    }

    [Theory]
    [InlineData(Seed.Creating, "Creating/-/-/-")]
    [InlineData(Seed.CreateFailed, "Failed/-/-/-")]
    [InlineData(Seed.Available, "Available/Available/-/-")]
    [InlineData(Seed.AttachRequested, "Attaching/Available/-/vm")]
    [InlineData(Seed.Attached, "Available/Available/vm/vm")]
    [InlineData(Seed.AttachedElsewhere, "Available/Available/-+elsewhere/-")]
    [InlineData(Seed.DetachRequested, "Detaching/Available/vm/-")]
    [InlineData(Seed.DeleteRequested, "Deleting/Available/-/-")]
    [InlineData(Seed.NotFound, "NotFound/-/-/-")]
    [InlineData(Seed.Deleted, "Deleted/-/-/-")]
    public void EverySituation_ReadsAsOneStatus_AttachmentAndWhetherIncusHoldsIt(Seed seed, string expected)
    {
        Assert.Equal(expected, Shape(VolumeIn(seed)));
    }

    // Counted toward MaxVolumes and billed (titan-ops features/volume/design.md#counting-and-billing): Incus holds the
    // volume, or is about to; a delete asked for stops the count at once but not the bill.
    [Theory]
    [InlineData(Seed.Creating, true, false)]
    [InlineData(Seed.CreateFailed, false, false)]
    [InlineData(Seed.Available, true, true)]
    [InlineData(Seed.AttachRequested, true, true)]
    [InlineData(Seed.Attached, true, true)]
    [InlineData(Seed.AttachedElsewhere, true, true)]
    [InlineData(Seed.DetachRequested, true, true)]
    [InlineData(Seed.DeleteRequested, false, true)]
    [InlineData(Seed.NotFound, false, false)]
    [InlineData(Seed.Deleted, false, false)]
    public void EverySituation_CountsTowardTheQuotaAndIsBilled_ByWhatIncusHolds(Seed seed, bool occupiesQuota, bool isBilled)
    {
        var v = VolumeIn(seed);

        Assert.Equal(occupiesQuota, v.OccupiesQuota);
        Assert.Equal(isBilled, v.IsBilled);
    }

    // An attach or detach waiting on a volume Incus no longer has counts for nothing: the status says an operation
    // waits, InstanceStatus says Incus does not hold the volume.
    [Fact]
    public void AnAttachWaiting_OnAVolumeIncusLost_NeitherCountsTowardTheQuotaNorIsBilled()
    {
        var v = VolumeIn(Seed.AttachRequested);

        v.MarkNotFound();

        Assert.Equal(VolumeStatus.Attaching, v.Status);
        Assert.Null(v.InstanceStatus);
        Assert.False(v.OccupiesQuota);
        Assert.False(v.IsBilled);
    }

    // ---------- create ----------

    [Fact]
    public void Created_SetsTheIdAndIsAvailable_AndFulfilsTheCreateOnce()
    {
        var v = VolumeIn(Seed.Creating, out Guid createRequestId);

        Assert.True(v.CreateSucceeded(createRequestId, "vol11", false, null, false));
        Assert.False(v.CreateSucceeded(createRequestId, "vol11", false, null, false));

        Assert.Equal("vol11", v.ExternalId);
        Assert.Equal(VolumeStatus.Available, v.Status);
        Assert.Null(v.Operation.PendingId);
    }

    [Fact]
    public void Created_Repeated_KeepsTheFirstId()
    {
        var v = VolumeIn(Seed.Creating);
        v.MarkCreated("vol11");
        v.MarkCreated("vol99");
        Assert.Equal("vol11", v.ExternalId);
    }

    // A Done must leave the volume named on Incus: with no id, stored or reported, it is refused before it writes anything.
    [Fact]
    public void Created_WithoutAnId_Throws_AndWritesNothing()
    {
        var v = VolumeIn(Seed.Creating, out Guid createRequestId);
        string before = Shape(v);

        var ex = Assert.Throws<BusinessException>(() => v.CreateSucceeded(createRequestId, null, true, AttachedVm.Id, false));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(before, Shape(v));
        Assert.Equal(createRequestId, v.Operation.PendingId);
        Assert.Null(v.ExternalId);
    }

    // The reconciler may see the volume before the create command reports: it records what it saw, but only the
    // command's report ends the create and brings the id.
    [Fact]
    public void Observed_OnAWaitingCreate_OnlyRecordsWhatIsSeen_AndLeavesTheCreateWaiting()
    {
        var v = VolumeIn(Seed.Creating);

        v.MarkObserved(null, false);

        Assert.Equal(VolumeStatus.Creating, v.Status);
        Assert.Equal(VolumeStatus.Available, v.Operation.LastSeenStatus);
        Assert.Equal(VolumeStatus.Available, v.InstanceStatus);
        Assert.Null(v.ExternalId);
    }

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsWhy_AndEndsIt_LeavingItFailed()
    {
        var v = VolumeIn(Seed.Creating);
        v.MarkRequestFailed("could not create", "raw");

        Assert.Null(v.Operation.PendingId);
        Assert.Equal(VolumeStatus.Failed, v.Status);
        Assert.Equal("could not create", v.Operation.FailureReason);
        Assert.Equal("raw", v.Operation.FailureDetail);
        Assert.NotNull(v.Operation.FailedAt);
    }

    // The report that names the provider id left a volume on Incus, which a delete can still reach.
    [Fact]
    public void RequestFailed_OnAWaitingCreate_ThatNamesTheVolume_LeavesItAvailable_WithTheReason()
    {
        var v = VolumeIn(Seed.Creating);

        v.MarkRequestFailed("could not finish", "raw", "vol11");

        Assert.Equal(VolumeStatus.Available, v.Status);
        Assert.Equal("vol11", v.ExternalId);
        Assert.Equal("could not finish", v.Operation.FailureReason);
        Assert.True(v.IsBilled);
    }

    // A report with no id leaves nothing on Incus to name, so the volume is Failed whatever the worker read:
    // it is not billed and not counted, and a delete removes it here.
    [Fact]
    public void RequestFailed_OnAWaitingCreate_ThatReadTheVolumeButBroughtNoId_LeavesItFailed_NotBilled()
    {
        var v = VolumeIn(Seed.Creating, out Guid createRequestId);

        Assert.True(v.CreateFailed(createRequestId, null, true, null, false, "could not create", "raw"));

        Assert.Equal(VolumeStatus.Failed, v.Status);
        Assert.False(v.IsBilled);
        Assert.False(v.OccupiesQuota);
    }

    [Theory]
    [InlineData(Seed.Available)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.DeleteRequested)]
    public void RequestFailed_WithNoCreateWaiting_IsStale_AndRecordsNothing(Seed seed)
    {
        var v = VolumeIn(seed, out Guid createRequestId);
        string before = Shape(v);

        Assert.False(v.CreateFailed(createRequestId, null, false, null, false, "late", "late"));

        Assert.Equal(before, Shape(v));
        Assert.Null(v.Operation.FailureReason);
    }

    // Not seeing the volume yet is not the create failing: the command still reports, and a create waiting counts
    // whatever the reconciler saw (titan-ops features/volume/design.md#counting-and-billing); only the report says
    // whether Incus holds the volume.
    [Fact]
    public void NotFound_KeepsAWaitingCreate()
    {
        var v = VolumeIn(Seed.Creating);

        v.MarkNotFound();

        Assert.Equal(VolumeStatus.Creating, v.Status);
        Assert.Null(v.Operation.LastSeenStatus);
        Assert.Null(v.InstanceStatus);
        Assert.True(v.OccupiesQuota);
        Assert.False(v.IsBilled);
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
        Assert.Equal(VolumeStatus.Attaching, v.Status);
        Assert.Equal(ids.OperationId, v.Operation.PendingId);
        Assert.Equal(VolumeStatus.Available, v.InstanceStatus);
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
        Assert.Equal("Available/Available/-/-", Shape(v));
        Assert.Null(v.Operation.PendingId);
    }

    [Fact]
    public void Attach_ToAVmInAnotherCluster_Throws()
    {
        var v = Volume.Create(TestTenant.WithRoomToSpare().Id, "vol-1", "cluster-b", "std", 20);
        v.MarkCreated("vol11");
        var ex = Assert.Throws<BusinessException>(() => v.Attach(VmForAttach(VmShape.Running)));
        Assert.Equal("VOLUME_CLUSTER_MISMATCH", ex.Code);
    }

    // No observation withdraws a waiting operation: an attach not seen yet keeps waiting for its command.
    [Fact]
    public void Observed_NotYetAttached_KeepsTheAttachWaiting()
    {
        var v = VolumeIn(Seed.AttachRequested);
        v.MarkObserved(null, false);
        Assert.Equal(VolumeStatus.Attaching, v.Status);
    }

    [Fact]
    public void Detach_FromAttached_AsksForNoVm_AndLeavesTheCurrentAttachmentAlone()
    {
        var v = VolumeIn(Seed.Attached);
        Assert.Equal("vol11", v.Detach().ProviderId);
        Assert.Null(v.DesiredAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.CurrentAttachedVmId);
        Assert.Equal(VolumeStatus.Detaching, v.Status);
        Assert.NotNull(v.Operation.PendingId);
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
        Assert.Equal(VolumeStatus.Detaching, v.Status);
        Assert.Equal(VolumeStatus.Available, v.Operation.LastSeenStatus);
    }

    [Fact]
    public void Observed_StillUsedOutsideTitan_KeepsTheDetachWaiting()
    {
        var v = VolumeIn(Seed.DetachRequested);
        v.MarkObserved(null, true);
        Assert.Equal(VolumeStatus.Detaching, v.Status);
    }

    // Attached or detached outside Titan, with nothing waiting: current follows Incus, desired stays — drift, no outcome.
    [Fact]
    public void Observed_AChangeMadeOutsideTitan_MovesOnlyTheCurrentAttachment()
    {
        var v = VolumeIn(Seed.Attached);
        v.MarkObserved(null, false);
        Assert.Null(v.CurrentAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
        Assert.Equal(VolumeStatus.Available, v.Status);
        Assert.Null(v.Operation.PendingId);

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

        Assert.True(GaveUp(v, attach.OperationId, "attach failed", true, null, false));

        Assert.Null(v.DesiredAttachedVmId);
        Assert.Equal(VolumeStatus.Available, v.Status);
        Assert.Null(v.Operation.PendingId);
        Assert.Equal("attach failed", v.Operation.FailureReason);
        Assert.NotNull(v.Attach(VmForAttach(VmShape.Running)));
    }

    [Fact]
    public void AFailedDetach_LeavesItAttached_WithTheReason()
    {
        var v = VolumeIn(Seed.Attached);
        OperationTicket detach = v.Detach();

        Assert.True(GaveUp(v, detach.OperationId, "detach failed", true, AttachedVm.Id, false));

        Assert.Equal(AttachedVm.Id, v.DesiredAttachedVmId);
        Assert.Equal(AttachedVm.Id, v.CurrentAttachedVmId);
        Assert.Equal(VolumeStatus.Available, v.Status);
        Assert.Equal("detach failed", v.Operation.FailureReason);
    }

    // The volume rests as Incus was last seen holding it, and the reconciler may have seen it gone while the command ran:
    // a give-up that read nothing then rests it NotFound; one that read the volume rests it Available.
    [Theory]
    [InlineData(Seed.AttachRequested, false, VolumeStatus.NotFound)]
    [InlineData(Seed.AttachRequested, true, VolumeStatus.Available)]
    [InlineData(Seed.DetachRequested, false, VolumeStatus.NotFound)]
    [InlineData(Seed.DetachRequested, true, VolumeStatus.Available)]
    public void AGiveUp_OfAnAttachOrDetach_OnAVolumeIncusLostWhileItWaited_RestsAsTheReportReadIt(
        Seed seed, bool sawTheVolume, VolumeStatus resting)
    {
        var v = VolumeIn(seed);
        v.MarkNotFound();
        Assert.Null(v.InstanceStatus);

        Assert.True(GaveUp(v, PendingOf(v), "failed", sawTheVolume, null, false));

        Assert.Equal(resting, v.Status);
        Assert.Null(v.Operation.PendingId);
        Assert.Equal("failed", v.Operation.FailureReason);
        Assert.Equal(v.CurrentAttachedVmId, v.DesiredAttachedVmId);
    }

    // Done rests the volume by the same rule as a give-up, with no failure and the attachment the worker read.
    [Fact]
    public void ADone_OfAnAttach_RestsAvailable_WithTheAttachmentTheWorkerRead_AndNoFailure()
    {
        var v = VolumeIn(Seed.Available);
        VolumeAttachProviderIds attach = v.Attach(AttachedVm);

        Assert.True(v.AttachSucceeded(attach.OperationId, null, true, AttachedVm.Id, false));

        Assert.Equal("Available/Available/vm/vm", Shape(v));
        Assert.Null(v.Operation.PendingId);
        Assert.Null(v.Operation.FailureReason);
    }

    // sawTheVolume false: the worker did not read the volume on Incus, so the attachment is not read either.
    [Fact]
    public void ADone_OfAnAttach_ThatReadNothing_RestsAsIncusWasLastSeen_AndWritesNoAttachment()
    {
        var v = VolumeIn(Seed.Available);
        VolumeAttachProviderIds attach = v.Attach(AttachedVm);

        Assert.True(v.AttachSucceeded(attach.OperationId, null, false, AttachedVm.Id, true));

        Assert.Equal("Available/Available/-/vm", Shape(v));
    }

    [Fact]
    public void ADone_OfAnAttachOrDetach_OnAVolumeIncusLostWhileItWaited_RestsNotFound()
    {
        var attaching = VolumeIn(Seed.AttachRequested);
        attaching.MarkNotFound();
        Assert.True(attaching.AttachSucceeded(PendingOf(attaching), null, false, null, false));
        Assert.Equal(VolumeStatus.NotFound, attaching.Status);

        var detaching = VolumeIn(Seed.DetachRequested);
        detaching.MarkNotFound();
        Assert.True(detaching.DetachSucceeded(PendingOf(detaching), null, false, null, false));
        Assert.Equal(VolumeStatus.NotFound, detaching.Status);
    }

    [Fact]
    public void ADone_OfADetach_RestsAvailable_WithTheAttachmentTheWorkerRead()
    {
        var v = VolumeIn(Seed.Attached);
        OperationTicket detach = v.Detach();

        Assert.True(v.DetachSucceeded(detach.OperationId, null, true, null, false));

        Assert.Equal("Available/Available/-/-", Shape(v));
    }

    // A failure stays on the volume while a later operation waits, and an operation that ends Done clears it.
    [Fact]
    public void ADone_ClearsTheLastFailure_WhichStaysUntilThen()
    {
        var v = VolumeIn(Seed.Available);
        Assert.True(GaveUp(v, v.Attach(AttachedVm).OperationId, "attach failed", true, null, false));
        VolumeAttachProviderIds again = v.Attach(AttachedVm);
        Assert.Equal("attach failed", v.Operation.FailureReason);

        Assert.True(v.AttachSucceeded(again.OperationId, null, true, AttachedVm.Id, false));

        Assert.Null(v.Operation.FailureReason);
        Assert.Null(v.Operation.FailureDetail);
        Assert.Null(v.Operation.FailedAt);
    }

    // Each method ends only the operation the volume's status says it waits in, and only the report carrying that
    // operation's id: any other report is ignored and writes nothing, the provider id and the attachment included.
    [Theory]
    [InlineData(Seed.Creating, VolumeStatus.Creating)]
    [InlineData(Seed.AttachRequested, VolumeStatus.Attaching)]
    [InlineData(Seed.DetachRequested, VolumeStatus.Detaching)]
    [InlineData(Seed.DeleteRequested, VolumeStatus.Deleting)]
    public void AReport_ForAnotherOperationThanTheOneWaiting_ChangesNothing_AndReturnsFalse(Seed seed, VolumeStatus waiting)
    {
        var v = VolumeIn(seed);
        Guid pending = PendingOf(v);
        string before = Shape(v);
        VolumeOperation operation = v.Operation;
        string? externalId = v.ExternalId;

        foreach (VolumeStatus method in Waiting.Where(w => w != waiting))
        {
            Assert.False(Succeeded(v, method, pending, "vol99", true, Guid.NewGuid(), true));
            Assert.False(GaveUp(v, method, pending, "late", true, Guid.NewGuid(), true, "vol99"));
        }
        foreach (VolumeStatus method in Waiting)
        {
            Assert.False(Succeeded(v, method, Guid.NewGuid(), "vol99", true, Guid.NewGuid(), true));
            Assert.False(GaveUp(v, method, Guid.NewGuid(), "late", true, Guid.NewGuid(), true, "vol99"));
        }

        Assert.Equal(before, Shape(v));
        Assert.Equal(operation, v.Operation);
        Assert.Equal(externalId, v.ExternalId);
        Assert.True(GaveUp(v, waiting, pending, "its own"));
    }

    // ---------- observation while an operation waits ----------

    // An operation waiting keeps its status whatever Incus shows; the observation records whether Incus holds the
    // volume (InstanceStatus) and writes the attachment as usual.
    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.AttachRequested)]
    [InlineData(Seed.DetachRequested)]
    [InlineData(Seed.DeleteRequested)]
    public void Observed_WhileAnOperationWaits_RecordsWhatIsSeen_AndTheStatusAndOperationStay(Seed seed)
    {
        var v = VolumeIn(seed);
        VolumeStatus status = v.Status;
        Guid pending = PendingOf(v);
        var other = VmForAttach(VmShape.Running);

        v.MarkNotFound();
        Assert.Equal(status, v.Status);
        Assert.Null(v.Operation.LastSeenStatus);
        Assert.Null(v.InstanceStatus);
        Assert.Null(v.CurrentAttachedVmId);

        v.MarkObserved(other.Id, true);
        Assert.Equal(status, v.Status);
        Assert.Equal(VolumeStatus.Available, v.Operation.LastSeenStatus);
        Assert.Equal(VolumeStatus.Available, v.InstanceStatus);
        Assert.Equal(other.Id, v.CurrentAttachedVmId);
        Assert.True(v.CurrentAttachedElsewhere);
        Assert.Equal(pending, v.Operation.PendingId);
    }

    // A create that gave up before Incus held a volume leaves nothing to observe, and the reconciler never lists a
    // volume without an id; an observation that still arrives is ignored rather than written.
    [Fact]
    public void Failed_IgnoresEveryObservation_AndNeverThrows()
    {
        var v = VolumeIn(Seed.CreateFailed, out Guid createRequestId);
        VolumeOperation before = v.Operation;

        v.MarkObserved(AttachedVm.Id, true);
        Assert.Equal("Failed/-/-/-", Shape(v));
        v.MarkNotFound();
        Assert.Equal("Failed/-/-/-", Shape(v));
        Assert.False(v.CreateSucceeded(createRequestId, "vol99", false, null, false));
        Assert.False(v.CreateFailed(createRequestId, null, false, null, false, "late", "late"));

        Assert.Equal(before, v.Operation);
        Assert.Null(v.ExternalId);
        Assert.False(v.IsBilled);
        Assert.False(v.OccupiesQuota);
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

    // A VM's delete is not an operation of the volume: it releases only an attachment asked for that VM, and never throws.
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
        Assert.Equal("Available/Available/vm/vm", Shape(v));
    }

    // ---------- delete ----------

    // A volume Incus no longer has is still deleted through Incus, which may know it again, and the delete remembers
    // whether Incus held it when it was pressed.
    [Theory]
    [InlineData(Seed.Available, VolumeStatus.Available)]
    [InlineData(Seed.NotFound, null)]
    public void RequestDelete_WithAnId_NotAttached_IsAccepted_AndYieldsTheId(Seed seed, VolumeStatus? heldWhenPressed)
    {
        var v = VolumeIn(seed);
        Assert.Equal("vol11", v.Delete()!.ProviderId);
        Assert.Equal(VolumeStatus.Deleting, v.Status);
        Assert.NotNull(v.Operation.PendingId);
        Assert.Equal(heldWhenPressed, v.InstanceStatus);
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
        Assert.Equal("Available/Available/-/-", Shape(v));
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
        Assert.Equal(VolumeStatus.Deleting, v.Status);
    }

    // The delete command's Done.
    [Fact]
    public void MarkDeleted_FulfilsTheDeleteOnce()
    {
        var v = VolumeIn(Seed.Available);
        OperationTicket delete = v.Delete()!;

        Assert.True(v.DeleteSucceeded(delete.OperationId, null, false, null, false));
        Assert.False(v.DeleteSucceeded(delete.OperationId, null, false, null, false));

        Assert.Equal(VolumeStatus.Deleted, v.Status);
        Assert.True(v.IsDeleted);
        Assert.Null(v.Operation.PendingId);
    }

    // The delete ends the attachment with the volume.
    [Fact]
    public void MarkDeleted_ClearsTheCurrentAttachment()
    {
        var v = VolumeIn(Seed.Available);
        OperationTicket delete = v.Delete()!;
        v.MarkObserved(AttachedVm.Id, true);

        Assert.True(v.DeleteSucceeded(delete.OperationId, null, true, AttachedVm.Id, true));

        Assert.Equal("Deleted/-/-/-", Shape(v));
    }

    // Only the delete command ends a delete: the reconciler finding the volume gone only records that Incus does not
    // hold it.
    [Fact]
    public void NotFound_KeepsAWaitingDelete()
    {
        var v = VolumeIn(Seed.DeleteRequested);
        Assert.Equal(VolumeStatus.Available, v.InstanceStatus);

        v.MarkNotFound();

        Assert.Equal(VolumeStatus.Deleting, v.Status);
        Assert.Null(v.InstanceStatus);
    }

    [Fact]
    public void AFailedDelete_GivesTheVolumeBack_WithTheReason_AndAnotherPressIsANewRequest()
    {
        var v = VolumeIn(Seed.Available);
        OperationTicket first = v.Delete()!;

        Assert.True(GaveUp(v, first.OperationId, "delete failed"));

        Assert.Equal("Available/Available/-/-", Shape(v));
        Assert.Equal("delete failed", v.Operation.FailureReason);

        OperationTicket second = v.Delete()!;
        Assert.NotEqual(first.OperationId, second.OperationId);
    }

    // The volume rests as Incus was last seen holding it: pressed on a volume Incus held, it is Available again unless
    // the reconciler saw it gone while the delete waited; pressed on a volume Incus lost, it is NotFound again unless the
    // report or the reconciler saw it.
    [Theory]
    [InlineData(Seed.Available, false, false, VolumeStatus.Available)]
    [InlineData(Seed.Available, true, false, VolumeStatus.NotFound)]
    [InlineData(Seed.NotFound, false, false, VolumeStatus.NotFound)]
    [InlineData(Seed.NotFound, false, true, VolumeStatus.Available)]
    public void AFailedDelete_RestsAsIncusWasLastSeen(Seed seed, bool lostWhileWaiting, bool readByTheReport,
        VolumeStatus resting)
    {
        var v = VolumeIn(seed);
        OperationTicket delete = v.Delete()!;
        if (lostWhileWaiting)
        {
            v.MarkNotFound();
        }

        Assert.True(v.DeleteFailed(delete.OperationId, null, readByTheReport, null, false, "delete failed", "raw"));

        Assert.Equal(resting, v.Status);
        Assert.Null(v.Operation.PendingId);
    }

    // A give-up names its operation; one the volume is not waiting on (another operation's, or none) is ignored.
    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Available)]
    [InlineData(Seed.Attached)]
    [InlineData(Seed.Deleted)]
    public void AGiveUp_ForARequestNotWaiting_ChangesNothing_AndReturnsFalse(Seed seed)
    {
        var v = VolumeIn(seed);
        string before = Shape(v);

        foreach (VolumeStatus method in Waiting)
        {
            Assert.False(GaveUp(v, method, Guid.NewGuid(), "late"));
        }

        Assert.Equal(before, Shape(v));
        Assert.Null(v.Operation.FailureReason);
    }

    [Fact]
    public void RequestDelete_OfACreateThatFailedBeforeIncusHadIt_DeletesAtOnce_WithNoTicket()
    {
        var v = VolumeIn(Seed.CreateFailed);
        Assert.Null(v.Delete());
        Assert.Equal("Deleted/-/-/-", Shape(v));
        Assert.Null(v.Operation.PendingId);
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
        foreach (VolumeStatus method in Waiting)
        {
            Assert.False(Succeeded(v, method, createRequestId, "vol99"));
            Assert.False(GaveUp(v, method, createRequestId, "late"));
        }
        v.MarkObserved(AttachedVm.Id, true);
        v.MarkNotFound();
        v.MarkDetachedByVmDelete(AttachedVm.Id);
        v.ReleaseFromVmDelete(AttachedVm.Id);
        Assert.Equal("Deleted/-/-/-", Shape(v));
        Assert.Equal("vol11", v.ExternalId);
        Assert.Null(v.Operation.FailureReason);
    }

    [Fact]
    public void NotFound_ThenSeenAgain_IsAvailableAgain()
    {
        var v = VolumeIn(Seed.NotFound);
        v.MarkObserved(null, false);
        Assert.Equal(VolumeStatus.Available, v.Status);
        Assert.Equal(VolumeStatus.Available, v.InstanceStatus);
    }

    // ================= volume snapshot =================

    public enum SnapSeed { Creating, CreateFailed, Exists, DeleteRequested, NotFound, Deleted }

    private static readonly SnapSeed[] AllSnapSeeds = Enum.GetValues<SnapSeed>();

    private static VolumeSnapshot SnapshotIn(SnapSeed seed) => SnapshotIn(seed, out _);

    private static VolumeSnapshot SnapshotIn(SnapSeed seed, out Guid createRequestId)
    {
        var s = VolumeIn(Seed.Available).WithSnapshot("snap-1");
        createRequestId = s.Operation.PendingId!.Value;
        switch (seed)
        {
            case SnapSeed.Creating: break;
            case SnapSeed.CreateFailed: s.MarkRequestFailed("could not create", "raw"); break;
            case SnapSeed.Exists: s.MarkCreated("vol11/s5"); break;
            case SnapSeed.DeleteRequested: s.MarkCreated("vol11/s5"); s.Delete(); break;
            case SnapSeed.NotFound: s.MarkCreated("vol11/s5"); s.MarkNotFound(); break;
            case SnapSeed.Deleted:
                s.MarkCreated("vol11/s5");
                s.DeleteSucceeded(s.Delete()!.OperationId);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return s;
    }

    // The status, then whether Incus holds the snapshot as far as Titan last knew.
    private static string SnapShape(VolumeSnapshot s)
    {
        string held = "-";
        if (s.InstanceStatus != null)
        {
            held = s.InstanceStatus.Value.ToString();
        }
        return $"{s.Status}/{held}";
    }

    [Theory]
    [InlineData(SnapSeed.Creating, "Creating/-")]
    [InlineData(SnapSeed.CreateFailed, "Failed/-")]
    [InlineData(SnapSeed.Exists, "Available/Available")]
    [InlineData(SnapSeed.DeleteRequested, "Deleting/Available")]
    [InlineData(SnapSeed.NotFound, "NotFound/-")]
    [InlineData(SnapSeed.Deleted, "Deleted/-")]
    public void Snapshot_EverySituation_ReadsAsOneStatus_AndWhetherIncusHoldsIt(SnapSeed seed, string expected)
    {
        Assert.Equal(expected, SnapShape(SnapshotIn(seed)));
    }

    // Counted toward MaxVolumeSnapshotsPerVolume (and so against the volume's delete) while creating or held; billed
    // while Incus holds it, a delete in flight included.
    [Theory]
    [InlineData(SnapSeed.Creating, true, false)]
    [InlineData(SnapSeed.CreateFailed, false, false)]
    [InlineData(SnapSeed.Exists, true, true)]
    [InlineData(SnapSeed.DeleteRequested, false, true)]
    [InlineData(SnapSeed.NotFound, false, false)]
    [InlineData(SnapSeed.Deleted, false, false)]
    public void Snapshot_EverySituation_CountsTowardTheQuotaAndIsBilled_ByWhatIncusHolds(
        SnapSeed seed, bool countsTowardQuota, bool isBilled)
    {
        var s = SnapshotIn(seed);

        Assert.Equal(countsTowardQuota, s.CountsTowardQuota);
        Assert.Equal(isBilled, s.IsBilled);
    }

    [Fact]
    public void Snapshot_Created_FulfilsTheCreateOnce()
    {
        var s = SnapshotIn(SnapSeed.Creating, out Guid createRequestId);
        Assert.True(s.CreateSucceeded(createRequestId, "vol11/s5"));
        Assert.False(s.CreateSucceeded(createRequestId, "vol11/s5"));
        s.MarkObserved();
        Assert.Equal("Available/Available", SnapShape(s));
    }

    // A Done must leave the snapshot named on Incus: with no id, stored or reported, it is refused before it writes anything.
    [Fact]
    public void Snapshot_Created_WithoutAnId_Throws_AndWritesNothing()
    {
        var s = SnapshotIn(SnapSeed.Creating, out Guid createRequestId);

        var ex = Assert.Throws<BusinessException>(() => s.CreateSucceeded(createRequestId, null));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal("Creating/-", SnapShape(s));
        Assert.Equal(createRequestId, s.Operation.PendingId);
    }

    [Fact]
    public void Snapshot_RequestFailed_RecordsWhy_OnlyWhileACreateWaits()
    {
        var s = SnapshotIn(SnapSeed.CreateFailed);
        Assert.Equal("could not create", s.Operation.FailureReason);
        Assert.Equal("raw", s.Operation.FailureDetail);
        Assert.Equal(VolumeSnapshotStatus.Failed, s.Status);

        var exists = SnapshotIn(SnapSeed.Exists, out Guid createRequestId);
        Assert.False(exists.CreateFailed(createRequestId, null, "late", "late"));
        Assert.Null(exists.Operation.FailureReason);
    }

    // The report that names the provider id left a snapshot on Incus, which a delete can still reach.
    [Fact]
    public void Snapshot_RequestFailed_ThatNamesTheSnapshot_LeavesItAvailable_WithTheReason()
    {
        var s = SnapshotIn(SnapSeed.Creating, out Guid createRequestId);

        Assert.True(s.CreateFailed(createRequestId, "vol11/s5", "could not finish", "raw"));

        Assert.Equal("Available/Available", SnapShape(s));
        Assert.Equal("vol11/s5", s.ExternalId);
        Assert.Equal("could not finish", s.Operation.FailureReason);
    }

    [Theory]
    [InlineData(SnapSeed.Exists)]
    [InlineData(SnapSeed.NotFound)]
    public void Snapshot_RequestDelete_WithAnId_IsAccepted(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        Assert.Equal("vol11/s5", s.Delete()!.ProviderId);
        Assert.Equal(VolumeSnapshotStatus.Deleting, s.Status);
        Assert.NotNull(s.Operation.PendingId);
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
        Assert.Equal("Deleting/-", SnapShape(s));
        Assert.True(s.DeleteSucceeded(delete.OperationId));
        Assert.False(s.DeleteSucceeded(delete.OperationId));
        Assert.True(s.IsDeleted);
    }

    [Fact]
    public void Snapshot_AFailedDelete_GivesItBack_WithTheReason()
    {
        var s = SnapshotIn(SnapSeed.Exists);
        OperationTicket delete = s.Delete()!;
        Assert.True(s.DeleteFailed(delete.OperationId, "delete failed", "raw"));
        Assert.Equal("Available/Available", SnapShape(s));
        Assert.Equal("delete failed", s.Operation.FailureReason);
        Assert.Equal("vol11/s5", s.Delete()!.ProviderId);
    }

    // The snapshot rests as Incus was last seen holding it, like a volume: pressed on one Incus lost it is NotFound again.
    [Theory]
    [InlineData(SnapSeed.Exists, false, VolumeSnapshotStatus.Available)]
    [InlineData(SnapSeed.Exists, true, VolumeSnapshotStatus.NotFound)]
    [InlineData(SnapSeed.NotFound, false, VolumeSnapshotStatus.NotFound)]
    public void Snapshot_AFailedDelete_RestsAsIncusWasLastSeen(SnapSeed seed, bool lostWhileWaiting,
        VolumeSnapshotStatus resting)
    {
        var s = SnapshotIn(seed);
        OperationTicket delete = s.Delete()!;
        if (lostWhileWaiting)
        {
            s.MarkNotFound();
        }

        Assert.True(s.DeleteFailed(delete.OperationId, "delete failed", "raw"));

        Assert.Equal(resting, s.Status);
        Assert.Null(s.Operation.PendingId);
    }

    // While an operation waits the observation records whether Incus holds the snapshot and nothing else.
    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.DeleteRequested)]
    public void Snapshot_Observed_WhileAnOperationWaits_RecordsWhatIsSeen_AndTheStatusAndOperationStay(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        VolumeSnapshotStatus status = s.Status;
        Guid pending = s.Operation.PendingId!.Value;

        s.MarkNotFound();
        Assert.Equal(status, s.Status);
        Assert.Null(s.InstanceStatus);

        s.MarkObserved();
        Assert.Equal(status, s.Status);
        Assert.Equal(VolumeSnapshotStatus.Available, s.InstanceStatus);
        Assert.Equal(pending, s.Operation.PendingId);
    }

    // A create waiting counts whatever the reconciler saw (titan-ops features/volume/design.md#counting-and-billing): only
    // the create's report says whether Incus holds the snapshot.
    [Fact]
    public void Snapshot_NotFound_DuringACreate_OnlyRecordsWhatIsSeen_AndTheSnapshotStillCounts()
    {
        var v = VolumeIn(Seed.Available);
        var s = v.WithSnapshot("snap-1");

        v.MarkSnapshotNotFound(s.Id);

        Assert.Equal(VolumeSnapshotStatus.Creating, s.Status);
        Assert.Null(s.InstanceStatus);
        Assert.True(s.CountsTowardQuota);
        Assert.Equal(1, v.SnapshotQuotaUsage);
    }

    // A give-up names its operation; one the snapshot is not waiting on (another operation's, or none) is ignored.
    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.Exists)]
    [InlineData(SnapSeed.Deleted)]
    public void Snapshot_AGiveUp_ForARequestNotWaiting_ChangesNothing_AndReturnsFalse(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        string before = SnapShape(s);

        Assert.False(s.CreateFailed(Guid.NewGuid(), null, "late", "late"));
        Assert.False(s.DeleteFailed(Guid.NewGuid(), "late", "late"));

        Assert.Equal(before, SnapShape(s));
        Assert.Null(s.Operation.FailureReason);
    }

    // Each method ends only the operation the snapshot's status says it waits in, even with the right id.
    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.DeleteRequested)]
    public void Snapshot_AReport_ForTheOtherOperationThanTheOneWaiting_ChangesNothing_AndReturnsFalse(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        Guid pending = s.Operation.PendingId!.Value;
        string before = SnapShape(s);
        VolumeSnapshotOperation operation = s.Operation;

        if (seed == SnapSeed.Creating)
        {
            Assert.False(s.DeleteSucceeded(pending));
            Assert.False(s.DeleteFailed(pending, "late", "late"));
        }
        else
        {
            Assert.False(s.CreateSucceeded(pending, "vol11/s9"));
            Assert.False(s.CreateFailed(pending, "vol11/s9", "late", "late"));
        }

        Assert.Equal(before, SnapShape(s));
        Assert.Equal(operation, s.Operation);
        Assert.NotEqual("vol11/s9", s.ExternalId);
    }

    [Fact]
    public void Snapshot_RequestDelete_DeletesHere_OnlyForACreateThatFailedBeforeIncusHadIt()
    {
        var failed = SnapshotIn(SnapSeed.CreateFailed);
        Assert.Null(failed.Delete());
        Assert.Equal("Deleted/-", SnapShape(failed));
        Assert.Null(failed.Operation.PendingId);

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
        Assert.False(s.CreateSucceeded(createRequestId, "vol11/s9"));
        s.MarkObserved();
        s.MarkNotFound();
        Assert.False(s.CreateFailed(createRequestId, null, "late", "late"));
        Assert.False(s.DeleteFailed(createRequestId, "late", "late"));
        Assert.False(s.DeleteSucceeded(createRequestId));
        s.MarkDeleted();
        Assert.Equal("Deleted/-", SnapShape(s));
        Assert.Equal("vol11/s5", s.ExternalId);
    }

    // A create that gave up before Incus held a snapshot leaves nothing to observe, and the reconciler never lists a
    // snapshot without an id; an observation that still arrives is ignored rather than written.
    [Fact]
    public void Snapshot_Failed_IgnoresEveryObservation_AndNeverThrows()
    {
        var s = SnapshotIn(SnapSeed.CreateFailed, out Guid createRequestId);
        VolumeSnapshotOperation before = s.Operation;

        s.MarkObserved();
        Assert.Equal("Failed/-", SnapShape(s));
        s.MarkNotFound();
        Assert.Equal("Failed/-", SnapShape(s));
        Assert.False(s.CreateSucceeded(createRequestId, "vol11/s9"));

        Assert.Equal(before, s.Operation);
        Assert.Null(s.ExternalId);
        Assert.False(s.IsBilled);
        Assert.False(s.CountsTowardQuota);
    }

    // The volume's delete Done ends a snapshot whatever it waited on, and leaves a Deleted one as it is.
    [Theory]
    [InlineData(SnapSeed.Creating)]
    [InlineData(SnapSeed.Exists)]
    [InlineData(SnapSeed.DeleteRequested)]
    [InlineData(SnapSeed.NotFound)]
    [InlineData(SnapSeed.CreateFailed)]
    [InlineData(SnapSeed.Deleted)]
    public void Snapshot_EndsWithItsVolume_FromEverySituation(SnapSeed seed)
    {
        var s = SnapshotIn(seed);
        string? externalId = s.ExternalId;

        s.EndWithItsVolume();

        Assert.Equal("Deleted/-", SnapShape(s));
        Assert.Null(s.Operation.PendingId);
        Assert.Equal(externalId, s.ExternalId);
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

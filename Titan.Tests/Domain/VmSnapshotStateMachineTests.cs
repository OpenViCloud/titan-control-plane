using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation, same split as VmStateMachineTests: a request
// throws when it cannot be accepted, an observation never throws and ends nothing, Deleted absorbs. MarkCreated,
// MarkReverted, MarkDeleted and MarkRequestFailed here are replays of the commands' reports (Fakes/ProvisionedOnTheProvider);
// how a request ends is VmSnapshotRequestLifecycleTests'. Incus has no status word for an instance snapshot, only whether
// it is there, so the one status says what waits or whether Incus holds it at rest (titan-ops features/vm/design.md#vm-snapshot).
// The Seed enum names every situation so each "from every other situation" test walks all of them.
public class VmSnapshotStateMachineTests
{
    public enum Seed { Creating, CreateFailed, Exists, RevertRequested, DeleteRequested, NotFound, Deleted }

    private static readonly Seed[] AllSeeds = Enum.GetValues<Seed>();

    private static Vm VmIn(VmStatus status)
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        if (status == VmStatus.Running) vm.MarkRunning("vm42", "10.0.0.5");
        else if (status == VmStatus.Stopped) { vm.MarkRunning("vm42", "10.0.0.5"); vm.MarkStopped(); }
        else if (status == VmStatus.Error) { vm.MarkRunning("vm42", "10.0.0.5"); vm.MarkObserved(VmStatus.Error, null); }
        Assert.Equal(status, vm.InstanceStatus);
        return vm;
    }

    // Fresh per call: the snapshots a test seeds would otherwise pile up on one VM and reach its cap.
    private static Vm RunningVm => VmIn(VmStatus.Running);

    // A snapshot is the VM's child entity (titan-ops architecture/code-structure.md#child-entity): seeded through the VM.
    private static VmSnapshot SnapshotIn(Seed seed)
    {
        var s = RunningVm.WithSnapshot("snap-1");
        switch (seed)
        {
            case Seed.Creating: break; // AddSnapshot() lands here
            case Seed.CreateFailed: s.MarkRequestFailed("could not create", "raw"); break;
            case Seed.Exists: s.MarkCreated("vm42/s7"); break;
            case Seed.RevertRequested: s.MarkCreated("vm42/s7"); s.Revert(Guid.NewGuid()); break;
            case Seed.DeleteRequested: s.MarkCreated("vm42/s7"); s.Delete(Guid.NewGuid()); break;
            case Seed.NotFound: s.MarkCreated("vm42/s7"); s.MarkNotFound(); break;
            case Seed.Deleted: s.MarkCreated("vm42/s7"); s.Delete(Guid.NewGuid()); s.MarkDeleted(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return s;
    }

    // The status, then whether Incus holds the snapshot as far as Titan last knew.
    private static string Shape(VmSnapshot s)
    {
        string held = "-";
        if (s.InstanceStatus != null)
        {
            held = s.InstanceStatus.Value.ToString();
        }
        return $"{s.Status}/{held}";
    }

    [Theory]
    [InlineData(Seed.Creating, "Creating/-")]
    [InlineData(Seed.CreateFailed, "Failed/-")]
    [InlineData(Seed.Exists, "Available/Available")]
    [InlineData(Seed.RevertRequested, "Reverting/Available")]
    [InlineData(Seed.DeleteRequested, "Deleting/Available")]
    [InlineData(Seed.NotFound, "NotFound/-")]
    [InlineData(Seed.Deleted, "Deleted/-")]
    public void EverySituation_ReadsAsOneStatus_AndWhetherIncusHoldsIt(Seed seed, string expected)
    {
        Assert.Equal(expected, Shape(SnapshotIn(seed)));
    }

    // ---------- create ----------

    [Theory]
    [InlineData(VmStatus.Error)]
    public void Create_FromAVmInError_Throws(VmStatus from)
    {
        var ex = Assert.Throws<BusinessException>(() => VmIn(from).AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s"));
        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    [Fact] // Incus snapshots and restores (disk only) a Stopped instance (titan-ops features/vm/design.md#requests-follow-incus)
    public void CreateAndRevert_OfAStoppedVm_AreAccepted()
    {
        Vm stopped = VmIn(VmStatus.Stopped);
        var s = stopped.WithSnapshot("ready");
        Assert.Equal(VmSnapshotStatus.Creating, s.Status);
        s.MarkCreated("vm42/s7");
        stopped.RevertSnapshot(s.Id, Guid.NewGuid());
        Assert.Equal("Reverting/Available", Shape(s));
    }

    [Fact]
    public void Created_SetsTheIdAndIsAvailable_AndFulfilsTheCreateOnce()
    {
        var s = SnapshotIn(Seed.Creating);
        Assert.True(s.MarkCreated("vm42/s7"));
        Assert.False(s.MarkCreated("vm42/s9"));
        Assert.Equal("vm42/s7", s.ExternalId);
        Assert.Equal("Available/Available", Shape(s));
    }

    // Only the create command's own report ends the create (titan-ops architecture/resource-lifecycle.md#invariants).
    [Fact]
    public void Observed_OnAWaitingCreate_EndsNothing()
    {
        var s = SnapshotIn(Seed.Creating);
        s.MarkObserved();
        Assert.Equal("Creating/Available", Shape(s));
    }

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsWhy_AndEndsIt()
    {
        var s = SnapshotIn(Seed.CreateFailed);
        Assert.Equal("could not create", s.Operation.FailureReason);
        Assert.Equal("raw", s.Operation.FailureDetail);
        Assert.NotNull(s.Operation.FailedAt);
        Assert.Equal(VmSnapshotStatus.Failed, s.Status);
        Assert.Null(s.Operation.PendingId);
    }

    [Theory]
    [InlineData(Seed.Exists)]
    [InlineData(Seed.RevertRequested)]
    [InlineData(Seed.DeleteRequested)]
    public void RequestFailed_WithNoCreateWaiting_IsStale_AndRecordsNothing(Seed seed)
    {
        var s = SnapshotIn(seed);
        string before = Shape(s);
        s.MarkRequestFailed("late", "late");
        Assert.Equal(before, Shape(s));
        Assert.Null(s.Operation.FailureReason);
    }

    // ---------- revert ----------

    [Fact]
    public void Revert_WhenIncusHasIt_WaitsForTheRevert_OnTheSnapshotsProviderId()
    {
        var s = SnapshotIn(Seed.Exists);
        Guid revert = Guid.NewGuid();
        s.Revert(revert);
        Assert.Equal("vm42/s7", s.GetExternalId());
        Assert.Equal(revert, s.Operation.PendingId);
        Assert.Equal("Reverting/Available", Shape(s));
    }

    [Fact] // the VM checks itself in the same call (Vm.RevertSnapshot), not the caller
    public void Revert_WithAVmInError_Throws_AndLeavesTheSnapshotAlone()
    {
        Vm vm = VmIn(VmStatus.Running);
        var s = vm.WithSnapshot("snap-1");
        s.MarkCreated("vm42/s7");
        vm.MarkObserved(VmStatus.Error, null);
        var ex = Assert.Throws<BusinessException>(() => vm.RevertSnapshot(s.Id, Guid.NewGuid()));
        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Equal("Available/Available", Shape(s));
    }

    [Fact]
    public void Revert_FromEveryOtherSituation_Throws_AndMutatesNothing()
    {
        foreach (Seed seed in AllSeeds.Where(s => s != Seed.Exists))
        {
            var s = SnapshotIn(seed);
            string before = Shape(s);
            var ex = Assert.Throws<BusinessException>(() => s.Revert(Guid.NewGuid()));
            Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
            Assert.Equal(before, Shape(s));
        }
    }

    [Fact]
    public void Reverted_EndsTheRevertOnce()
    {
        var s = SnapshotIn(Seed.RevertRequested);
        Assert.True(s.MarkReverted());
        Assert.False(s.MarkReverted());
        Assert.Equal("Available/Available", Shape(s));
    }

    // Seeing the snapshot proves it exists, not that a revert finished.
    [Fact]
    public void Observed_NeverEndsARevert()
    {
        var s = SnapshotIn(Seed.RevertRequested);
        s.MarkObserved();
        Assert.Equal(VmSnapshotStatus.Reverting, s.Status);
    }

    [Fact]
    public void Reverted_WithNoRevertWaiting_FulfilsNothing()
    {
        Assert.False(SnapshotIn(Seed.Exists).MarkReverted());
        var deleting = SnapshotIn(Seed.DeleteRequested);
        Assert.False(deleting.MarkReverted());
        Assert.Equal(VmSnapshotStatus.Deleting, deleting.Status);
    }

    [Fact]
    public void AFailedRevert_EndsIt_WithTheReason_AndKeepsTheSnapshot()
    {
        var s = SnapshotIn(Seed.RevertRequested);
        Assert.True(s.RevertFailed(s.Operation.PendingId!.Value, "revert failed", "raw"));
        Assert.Equal("Available/Available", Shape(s));
        Assert.Equal("revert failed", s.Operation.FailureReason);
    }

    // ---------- delete ----------

    [Theory]
    [InlineData(Seed.Exists)]
    [InlineData(Seed.NotFound)]
    public void RequestDelete_WithAnId_IsAccepted(Seed seed)
    {
        var s = SnapshotIn(seed);
        s.Delete(Guid.NewGuid());
        Assert.Equal("vm42/s7", s.GetExternalId());
        Assert.Equal(VmSnapshotStatus.Deleting, s.Status);
        Assert.True(s.IsDeletedOrDeleting);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.RevertRequested)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    public void RequestDelete_FromEveryOtherSituation_Throws_AndMutatesNothing(Seed seed)
    {
        var s = SnapshotIn(seed);
        string before = Shape(s);
        var ex = Assert.Throws<BusinessException>(() => s.Delete(Guid.NewGuid()));
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
        Assert.Equal(before, Shape(s));
    }

    [Fact]
    public void NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var s = SnapshotIn(Seed.DeleteRequested);
        s.MarkNotFound();
        Assert.Equal("Deleting/-", Shape(s));
        Assert.True(s.MarkDeleted());
        Assert.False(s.MarkDeleted());
        Assert.True(s.IsDeleted);
    }

    [Fact]
    public void AFailedDelete_GivesTheSnapshotBack_WithTheReason()
    {
        var s = SnapshotIn(Seed.DeleteRequested);
        Assert.True(s.DeleteFailed(s.Operation.PendingId!.Value, "delete failed", "raw"));
        Assert.Equal("Available/Available", Shape(s));
        Assert.Equal("delete failed", s.Operation.FailureReason);
        s.Delete(Guid.NewGuid());
        Assert.Equal(VmSnapshotStatus.Deleting, s.Status);
        Assert.Equal("vm42/s7", s.GetExternalId());
    }

    [Fact]
    public void RequestDelete_DeletesHere_OnlyForACreateThatFailedBeforeIncusHadIt()
    {
        var failed = SnapshotIn(Seed.CreateFailed);
        failed.Delete(Guid.NewGuid());
        Assert.True(failed.IsDeleted);
        Assert.Equal("Deleted/-", Shape(failed));
        Assert.Null(failed.Operation.PendingId);

        foreach (Seed seed in new[] { Seed.Creating, Seed.Exists, Seed.NotFound, Seed.Deleted })
        {
            Assert.False(DeletedHere(SnapshotIn(seed)));
        }
    }

    // A refused delete deleted nothing here; an accepted one deleted here only when it left the snapshot Deleted.
    private static bool DeletedHere(VmSnapshot snapshot)
    {
        try
        {
            snapshot.Delete(Guid.NewGuid());
        }
        catch (BusinessException)
        {
            return false;
        }
        return snapshot.IsDeleted;
    }

    // ---------- NotFound, Deleted ----------

    [Fact]
    public void NotFound_KeepsAWaitingCreate_AndSeenAgainIsAvailableAgain()
    {
        var s = SnapshotIn(Seed.Creating);
        s.MarkNotFound();
        Assert.Equal("Creating/-", Shape(s));

        var lost = SnapshotIn(Seed.NotFound);
        lost.MarkObserved();
        Assert.Equal("Available/Available", Shape(lost));
    }

    // A changed rule (titan-ops features/vm/design.md#vm-snapshot-counting): a create waiting counts whatever the reconciler
    // saw, since only the create's report says whether Incus holds the snapshot; before the one status, a create seen
    // missing stopped counting.
    [Fact]
    public void NotFound_DuringACreate_OnlyRecordsWhatIsSeen_AndTheSnapshotStillCounts()
    {
        Vm vm = RunningVm;
        var s = vm.WithSnapshot("snap-1");

        vm.MarkSnapshotNotFound(s.Id);

        Assert.Equal(VmSnapshotStatus.Creating, s.Status);
        Assert.Null(s.InstanceStatus);
        Assert.True(s.CountsTowardQuota);
        Assert.Equal(1, vm.SnapshotQuotaUsage);
    }

    [Fact]
    public void Deleted_IgnoresEveryObservation_AndNeverThrows()
    {
        var s = SnapshotIn(Seed.Deleted);
        Assert.False(s.MarkCreated("vm42/s9"));
        s.MarkObserved();
        Assert.False(s.MarkReverted());
        s.MarkNotFound();
        s.MarkRequestFailed("late", "late");
        Assert.False(s.MarkDeleted());
        Assert.Equal("Deleted/-", Shape(s));
    }

    // A changed rule: a create that gave up before Incus held a snapshot leaves nothing to observe, and the reconciler
    // never lists a snapshot without an id; an observation that still arrives is ignored rather than written.
    [Fact]
    public void Failed_IgnoresEveryObservation_AndNeverThrows()
    {
        var s = SnapshotIn(Seed.CreateFailed);
        VmSnapshotOperation before = s.Operation;

        s.MarkObserved();
        Assert.Equal("Failed/-", Shape(s));
        s.MarkNotFound();
        Assert.Equal("Failed/-", Shape(s));
        Assert.False(s.MarkCreated("vm42/s9"));
        Assert.False(s.MarkReverted());
        Assert.False(s.MarkDeleted());

        Assert.Equal(before, s.Operation);
        Assert.Null(s.ExternalId);
        Assert.False(s.IsBilled);
        Assert.False(s.CountsTowardQuota);
    }
}

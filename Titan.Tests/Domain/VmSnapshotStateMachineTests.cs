using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/intent-and-observation.md#intent-versus-observation, same split as VmStateMachineTests: a request
// throws when it cannot be accepted, an observation (Mark*) never throws, Deleted absorbs. Incus has no status word
// for an instance snapshot, so the model is existence plus the waiting request (titan-ops features/vm/design.md#vm-snapshot-is-existence).
// The Seed enum names every situation so each "from every other situation" test walks all of them.
public class VmSnapshotStateMachineTests
{
    public enum Seed { Creating, CreateFailed, Exists, RevertRequested, DeleteRequested, NotFound, Deleted }

    private static readonly Seed[] AllSeeds = Enum.GetValues<Seed>();

    private static Vm VmIn(VmState status)
    {
        var vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        if (status == VmState.Running) vm.MarkRunning("vm42", "10.0.0.5");
        else if (status == VmState.Stopped) { vm.MarkRunning("vm42", "10.0.0.5"); vm.MarkStopped(); }
        else if (status == VmState.Error) { vm.MarkRunning("vm42", "10.0.0.5"); vm.MarkObserved(VmState.Error, null); }
        Assert.Equal(status, vm.CurrentState);
        return vm;
    }

    private static readonly Vm RunningVm = VmIn(VmState.Running);

    private static VmSnapshot SnapshotIn(Seed seed)
    {
        var s = VmSnapshot.Create(RunningVm, TestTenant.WithRoomToSpare(), 0, "snap-1");
        switch (seed)
        {
            case Seed.Creating: break; // Create() lands here
            case Seed.CreateFailed: s.MarkRequestFailed("could not create", "raw"); break;
            case Seed.Exists: s.MarkCreated("vm42/s7"); break;
            case Seed.RevertRequested: s.MarkCreated("vm42/s7"); s.Revert(RunningVm); break;
            case Seed.DeleteRequested: s.MarkCreated("vm42/s7"); s.RequestDelete(); break;
            case Seed.NotFound: s.MarkCreated("vm42/s7"); s.MarkNotFound(); break;
            case Seed.Deleted: s.MarkCreated("vm42/s7"); s.RequestDelete(); s.MarkDeleted(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return s;
    }

    private static string Shape(VmSnapshot s)
    {
        string transition = "-";
        if (s.Transition != null)
        {
            transition = s.Transition.Value.ToString();
        }
        return $"{s.CurrentExistence}/{s.DesiredExistence}/{transition}";
    }

    [Theory]
    [InlineData(Seed.Creating, "NotCreated/Exists/Creating")]
    [InlineData(Seed.CreateFailed, "NotCreated/Exists/-")]
    [InlineData(Seed.Exists, "Exists/Exists/-")]
    [InlineData(Seed.RevertRequested, "Exists/Exists/Reverting")]
    [InlineData(Seed.DeleteRequested, "Exists/Deleted/Deleting")]
    [InlineData(Seed.NotFound, "NotFound/Exists/-")]
    [InlineData(Seed.Deleted, "Deleted/Deleted/-")]
    public void EverySituation_ReadsAsExistenceAndTransition(Seed seed, string expected)
    {
        Assert.Equal(expected, Shape(SnapshotIn(seed)));
    }

    // ---------- create ----------

    [Theory]
    [InlineData(VmState.Error)]
    public void Create_FromAVmInError_Throws(VmState from)
    {
        var ex = Assert.Throws<BusinessException>(() => VmSnapshot.Create(VmIn(from), TestTenant.WithRoomToSpare(), 0, "s"));
        Assert.Equal("VM_INVALID_STATUS", ex.Code);
    }

    [Fact] // Incus snapshots and restores (disk only) a Stopped instance (titan-ops features/vm/design.md#requests-follow-incus)
    public void CreateAndRevert_OfAStoppedVm_AreAccepted()
    {
        Vm stopped = VmIn(VmState.Stopped);
        var created = VmSnapshot.Create(stopped, TestTenant.WithRoomToSpare(), 0, "s");
        Assert.Equal(VmSnapshotTransition.Creating, created.Transition);

        var s = SnapshotIn(Seed.Exists);
        s.Revert(VmIn(VmState.Stopped));
        Assert.Equal("Exists/Exists/Reverting", Shape(s));
    }

    [Fact]
    public void Created_SetsTheIdAndExists_AndFulfilsTheCreateOnce()
    {
        var s = SnapshotIn(Seed.Creating);
        Assert.True(s.MarkCreated("vm42/s7"));
        Assert.False(s.MarkCreated("vm42/s9"));
        Assert.Equal("vm42/s7", s.ExternalId);
        Assert.Equal("Exists/Exists/-", Shape(s));
    }

    // The Created report was lost: the reconciler only lists snapshots with an id, but a give-up may bring the id.
    [Fact]
    public void Observed_OnAWaitingCreate_FulfilsIt()
    {
        var s = SnapshotIn(Seed.Creating);
        Assert.True(s.MarkObserved());
        Assert.Equal("Exists/Exists/-", Shape(s));
    }

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsWhy_AndEndsIt()
    {
        var s = SnapshotIn(Seed.CreateFailed);
        Assert.Equal("could not create", s.RequestFailureReason);
        Assert.Equal("raw", s.RequestFailureDetail);
        Assert.NotNull(s.RequestFailedAt);
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
        Assert.Null(s.RequestFailureReason);
    }

    // ---------- revert ----------

    [Fact]
    public void Revert_WhenIncusHasIt_WaitsForTheRevert_AndYieldsTheId()
    {
        var s = SnapshotIn(Seed.Exists);
        Assert.Equal("vm42/s7", s.Revert(RunningVm));
        Assert.Equal("Exists/Exists/Reverting", Shape(s));
    }

    [Fact] // revert checks the VM in the same call, not by the caller
    public void Revert_WithAVmInError_Throws_AndLeavesTheSnapshotAlone()
    {
        var s = SnapshotIn(Seed.Exists);
        var ex = Assert.Throws<BusinessException>(() => s.Revert(VmIn(VmState.Error)));
        Assert.Equal("VM_INVALID_STATUS", ex.Code);
        Assert.Equal("Exists/Exists/-", Shape(s));
    }

    [Fact]
    public void Revert_FromEveryOtherSituation_Throws_AndMutatesNothing()
    {
        foreach (Seed seed in AllSeeds.Where(s => s != Seed.Exists))
        {
            var s = SnapshotIn(seed);
            string before = Shape(s);
            var ex = Assert.Throws<BusinessException>(() => s.Revert(RunningVm));
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
        Assert.Equal("Exists/Exists/-", Shape(s));
    }

    // Seeing the snapshot proves it exists, not that a revert finished.
    [Fact]
    public void Observed_NeverEndsARevert()
    {
        var s = SnapshotIn(Seed.RevertRequested);
        Assert.False(s.MarkObserved());
        Assert.Equal(VmSnapshotTransition.Reverting, s.Transition);
    }

    [Fact]
    public void Reverted_WithNoRevertWaiting_FulfilsNothing()
    {
        Assert.False(SnapshotIn(Seed.Exists).MarkReverted());
        var deleting = SnapshotIn(Seed.DeleteRequested);
        Assert.False(deleting.MarkReverted());
        Assert.Equal(VmSnapshotTransition.Deleting, deleting.Transition);
    }

    [Fact]
    public void AFailedRevert_EndsIt_WithTheReason_AndKeepsTheSnapshot()
    {
        var s = SnapshotIn(Seed.RevertRequested);
        s.AbandonRequest("revert failed", "raw");
        Assert.Equal("Exists/Exists/-", Shape(s));
        Assert.Equal("revert failed", s.RequestFailureReason);
    }

    // ---------- delete ----------

    [Theory]
    [InlineData(Seed.Exists)]
    [InlineData(Seed.NotFound)]
    public void RequestDelete_WithAnId_IsAccepted(Seed seed)
    {
        var s = SnapshotIn(seed);
        Assert.Equal("vm42/s7", s.RequestDelete());
        Assert.Equal(VmSnapshotTransition.Deleting, s.Transition);
        Assert.Equal(DesiredExistence.Deleted, s.DesiredExistence);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.RevertRequested)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    public void RequestDelete_FromEveryOtherSituation_Throws_AndMutatesNothing(Seed seed)
    {
        var s = SnapshotIn(seed);
        string before = Shape(s);
        var ex = Assert.Throws<BusinessException>(() => s.RequestDelete());
        Assert.Equal("VM_SNAPSHOT_INVALID_STATE", ex.Code);
        Assert.Equal(before, Shape(s));
    }

    [Fact]
    public void NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var s = SnapshotIn(Seed.DeleteRequested);
        s.MarkNotFound();
        Assert.Equal(VmSnapshotTransition.Deleting, s.Transition);
        Assert.True(s.MarkDeleted());
        Assert.False(s.MarkDeleted());
        Assert.True(s.IsTerminal);
    }

    [Fact]
    public void AFailedDelete_GivesTheSnapshotBack_WithTheReason()
    {
        var s = SnapshotIn(Seed.DeleteRequested);
        s.AbandonRequest("delete failed", "raw");
        Assert.Equal("Exists/Exists/-", Shape(s));
        Assert.Equal("delete failed", s.RequestFailureReason);
        Assert.Equal("vm42/s7", s.RequestDelete());
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Exists)]
    [InlineData(Seed.Deleted)]
    public void AbandonRequest_WithNoRevertOrDeleteWaiting_Throws(Seed seed)
    {
        Assert.Throws<BusinessException>(() => SnapshotIn(seed).AbandonRequest("x", "y"));
    }

    [Fact]
    public void DeleteLocally_OnlyForACreateThatFailedBeforeIncusHadIt()
    {
        var failed = SnapshotIn(Seed.CreateFailed);
        failed.DeleteLocally();
        Assert.Equal("Deleted/Deleted/-", Shape(failed));

        foreach (Seed seed in new[] { Seed.Creating, Seed.Exists, Seed.NotFound, Seed.Deleted })
        {
            Assert.Throws<BusinessException>(() => SnapshotIn(seed).DeleteLocally());
        }
    }

    // ---------- NotFound, Deleted ----------

    [Fact]
    public void NotFound_EndsAWaitingCreate_AndSeenAgainExistsAgain()
    {
        var s = SnapshotIn(Seed.Creating);
        s.MarkNotFound();
        Assert.Equal("NotFound/Exists/-", Shape(s));

        var lost = SnapshotIn(Seed.NotFound);
        lost.MarkObserved();
        Assert.Equal("Exists/Exists/-", Shape(lost));
    }

    [Fact]
    public void Deleted_IgnoresEveryObservation_AndNeverThrows()
    {
        var s = SnapshotIn(Seed.Deleted);
        Assert.False(s.MarkCreated("vm42/s9"));
        Assert.False(s.MarkObserved());
        Assert.False(s.MarkReverted());
        s.MarkNotFound();
        s.MarkRequestFailed("late", "late");
        Assert.False(s.MarkDeleted());
        Assert.Equal("Deleted/Deleted/-", Shape(s));
    }
}

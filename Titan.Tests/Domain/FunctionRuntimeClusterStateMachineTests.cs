using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The moves of a function runtime in one function cluster (titan-ops features/ovc-function/design.md#runtimes): the
// four statuses of a VM template in a cluster (titan-ops features/catalog/design.md#template-state-is-existence), with
// no provider id: the image digest is the runtime's id on the cluster. A sync throws when it cannot be accepted, a
// report ends only the sync it names, an observation never throws and ends nothing. The Seed enum names every situation
// a cluster can be in.
public class FunctionRuntimeClusterStateMachineTests
{
    private const string Fn = "fn-dev";
    private const string Other = "fn-other";
    private const string Reason = "The runtime image could not be pulled onto every node of the cluster.";
    private const string Image =
        "ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:c2c3c3e141ea062fa0cb4c11119c8bf45184d98a22f7f384456fd4082883b8d1";

    public enum Seed
    {
        ObservedGone,          // no row until the reconciler found the image missing: NotFound
        ObservedThere,         // no row until the reconciler found the image on every Ready node: Available
        FirstSyncWaiting,      // Syncing, never on the cluster
        FirstSyncWaitingSeen,  // Syncing, never on the cluster before, the reconciler saw it
        FirstSyncGaveUp,       // Failed
        Available,
        ImageLost,             // NotFound: it was there, the reconciler found it missing
        ReSyncWaitingNotSeen,  // Syncing from NotFound, the reconciler did not find it
        ReSyncWaitingSeen,     // Syncing from NotFound, the reconciler saw it
    }

    private static FunctionRuntime NewRuntime(DateOnly? supportEndsOn = null) =>
        FunctionRuntime.Create("python3.15", Image, "amd64", supportEndsOn);

    private static FunctionRuntime RuntimeIn(Seed seed)
    {
        FunctionRuntime r = NewRuntime();
        switch (seed)
        {
            case Seed.ObservedGone: r.MarkNotFoundIn(Fn); break;
            case Seed.ObservedThere: r.MarkObservedIn(Fn); break;
            case Seed.FirstSyncWaiting: r.SyncIn(Fn); break;
            case Seed.FirstSyncWaitingSeen: r.SyncIn(Fn); r.MarkObservedIn(Fn); break;
            case Seed.FirstSyncGaveUp: r.SyncFailedIn(Fn, r.SyncIn(Fn), Reason, "raw"); break;
            case Seed.Available: r.SyncSucceededIn(Fn, r.SyncIn(Fn)); break;
            case Seed.ImageLost: r.SyncSucceededIn(Fn, r.SyncIn(Fn)); r.MarkNotFoundIn(Fn); break;
            case Seed.ReSyncWaitingNotSeen: r.SyncSucceededIn(Fn, r.SyncIn(Fn)); r.MarkNotFoundIn(Fn); r.SyncIn(Fn); break;
            case Seed.ReSyncWaitingSeen:
                r.SyncSucceededIn(Fn, r.SyncIn(Fn)); r.MarkNotFoundIn(Fn); r.SyncIn(Fn); r.MarkObservedIn(Fn); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return r;
    }

    private static FunctionRuntimeCluster Row(FunctionRuntime r) => r.Clusters.Single(c => c.ClusterId == Fn);

    private static bool Waits(Seed seed) =>
        seed is Seed.FirstSyncWaiting or Seed.FirstSyncWaitingSeen or Seed.ReSyncWaitingNotSeen or Seed.ReSyncWaitingSeen;

    // ---------- the root ----------

    [Fact]
    public void Create_IsListed_WithNoCluster()
    {
        FunctionRuntime r = FunctionRuntime.Create("go", Image, "amd64", null);

        Assert.Equal(("go", Image, "amd64", true, (DateOnly?)null), (r.Name, r.Image, r.Architecture, r.IsListed, r.SupportEndsOn));
        Assert.NotEqual(Guid.Empty, r.Id);
        Assert.Empty(r.Clusters);
    }

    // The worker refuses an image not pinned by digest: a tag can move under a runtime that customers deploy on.
    [Theory]
    [InlineData("ghcr.io/openvicloud/titan-function-runtimes/go:latest")]
    [InlineData("")]
    public void Create_RefusesAnImageNotPinnedByDigest(string image)
    {
        Assert.Throws<ArgumentException>(() => FunctionRuntime.Create("go", image, "amd64", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ACluster_MustBeNamed(string clusterId)
    {
        FunctionRuntime r = NewRuntime();

        Assert.Throws<ArgumentException>(() => r.SyncIn(clusterId));
        Assert.Throws<ArgumentException>(() => r.MarkObservedIn(clusterId));
        Assert.Throws<ArgumentException>(() => r.MarkNotFoundIn(clusterId));
    }

    // ---------- what each situation reads ----------

    [Theory]
    [InlineData(Seed.ObservedGone, FunctionRuntimeClusterStatus.NotFound, false)]
    [InlineData(Seed.ObservedThere, FunctionRuntimeClusterStatus.Available, true)]
    [InlineData(Seed.FirstSyncWaiting, FunctionRuntimeClusterStatus.Syncing, false)]
    [InlineData(Seed.FirstSyncWaitingSeen, FunctionRuntimeClusterStatus.Syncing, true)]
    [InlineData(Seed.FirstSyncGaveUp, FunctionRuntimeClusterStatus.Failed, false)]
    [InlineData(Seed.Available, FunctionRuntimeClusterStatus.Available, true)]
    [InlineData(Seed.ImageLost, FunctionRuntimeClusterStatus.NotFound, false)]
    [InlineData(Seed.ReSyncWaitingNotSeen, FunctionRuntimeClusterStatus.Syncing, false)]
    [InlineData(Seed.ReSyncWaitingSeen, FunctionRuntimeClusterStatus.Syncing, true)]
    public void EverySituation_ReadsItsStatus_AndWhetherEveryNodeHoldsTheImage(Seed seed,
        FunctionRuntimeClusterStatus status, bool available)
    {
        FunctionRuntime r = RuntimeIn(seed);

        Assert.Equal(status, Row(r).Status);
        Assert.Equal(available, Row(r).IsAvailable);
        Assert.Equal(Waits(seed), Row(r).Operation.PendingId != null);
    }

    // ---------- Sync ----------

    [Theory] // from no row, a Failed one and a NotFound one; Syncing and Available refuse
    [InlineData(Seed.ObservedGone, true)]
    [InlineData(Seed.ObservedThere, false)]
    [InlineData(Seed.FirstSyncWaiting, false)]
    [InlineData(Seed.FirstSyncWaitingSeen, false)]
    [InlineData(Seed.FirstSyncGaveUp, true)]
    [InlineData(Seed.Available, false)]
    [InlineData(Seed.ImageLost, true)]
    [InlineData(Seed.ReSyncWaitingNotSeen, false)]
    [InlineData(Seed.ReSyncWaitingSeen, false)]
    public void Sync_IsAccepted_OnlyWhereNothingHoldsOrWaits(Seed seed, bool accepted)
    {
        FunctionRuntime r = RuntimeIn(seed);
        Assert.Equal(accepted, r.NeedsSyncIn(Fn));

        if (!accepted)
        {
            Guid? before = Row(r).Operation.PendingId;
            var e = Assert.Throws<BusinessException>(() => r.SyncIn(Fn));
            Assert.Equal("INVALID_STATUS", e.Code);
            Assert.Equal(before, Row(r).Operation.PendingId);
            return;
        }

        Guid requestId = r.SyncIn(Fn);

        Assert.Equal(FunctionRuntimeClusterStatus.Syncing, Row(r).Status);
        Assert.Equal(requestId, Row(r).Operation.PendingId);
        Assert.NotNull(Row(r).Operation.AskedAt);
    }

    [Fact]
    public void Sync_OfAClusterWithNoRow_AddsOneWaiting_AndLeavesTheOtherClustersAlone()
    {
        FunctionRuntime r = NewRuntime();
        r.SyncSucceededIn(Other, r.SyncIn(Other));

        Guid requestId = r.SyncIn(Fn);

        Assert.True(r.NeedsSyncIn("fn-third"));
        Assert.Equal((FunctionRuntimeClusterStatus.Syncing, (Guid?)requestId), (Row(r).Status, Row(r).Operation.PendingId));
        Assert.Equal(FunctionRuntimeClusterStatus.Available, r.Clusters.Single(c => c.ClusterId == Other).Status);
    }

    // ---------- the report of the sync's own command ----------

    [Theory]
    [InlineData(Seed.FirstSyncWaiting)]
    [InlineData(Seed.FirstSyncWaitingSeen)]
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void Done_NamingTheWaitingSync_IsAvailable_AndClearsAnOlderFailure(Seed seed)
    {
        FunctionRuntime r = RuntimeIn(seed);
        Guid requestId = Row(r).Operation.PendingId!.Value;

        Assert.True(r.SyncSucceededIn(Fn, requestId));

        Assert.Equal(FunctionRuntimeClusterStatus.Available, Row(r).Status);
        Assert.Equal(FunctionRuntimeOperation.None, Row(r).Operation);
    }

    [Fact]
    public void Done_AfterAFailedSync_ClearsTheReason()
    {
        FunctionRuntime r = RuntimeIn(Seed.FirstSyncGaveUp);
        Guid requestId = r.SyncIn(Fn);
        Assert.Equal(Reason, Row(r).Operation.FailureReason); // shown until a sync ends Done

        r.SyncSucceededIn(Fn, requestId);

        Assert.Null(Row(r).Operation.FailureReason);
        Assert.Null(Row(r).Operation.FailedAt);
    }

    // Failed rests on what the runtime was known to be: Available when the sync saw every Ready node hold it, NotFound
    // when it had been on the cluster, Failed when it never was.
    [Theory]
    [InlineData(Seed.FirstSyncWaiting, FunctionRuntimeClusterStatus.Failed)]
    [InlineData(Seed.FirstSyncWaitingSeen, FunctionRuntimeClusterStatus.Available)]
    [InlineData(Seed.ReSyncWaitingNotSeen, FunctionRuntimeClusterStatus.NotFound)]
    [InlineData(Seed.ReSyncWaitingSeen, FunctionRuntimeClusterStatus.Available)]
    public void Failed_NamingTheWaitingSync_RecordsTheReason_AndRestsOnWhatWasKnown(Seed seed,
        FunctionRuntimeClusterStatus resting)
    {
        FunctionRuntime r = RuntimeIn(seed);
        Guid requestId = Row(r).Operation.PendingId!.Value;

        Assert.True(r.SyncFailedIn(Fn, requestId, Reason, "raw"));

        Assert.Equal(resting, Row(r).Status);
        Assert.Equal((Reason, "raw"), (Row(r).Operation.FailureReason, Row(r).Operation.FailureDetail));
        Assert.NotNull(Row(r).Operation.FailedAt);
        Assert.Null(Row(r).Operation.PendingId);
    }

    // A NotFound row the reconciler made (no row before) gave the image no chance to be there either; it rests NotFound
    // again, as the row was before the sync.
    [Fact]
    public void Failed_OfASyncFromAnObservedGoneRow_RestsNotFound()
    {
        FunctionRuntime r = RuntimeIn(Seed.ObservedGone);
        Guid requestId = r.SyncIn(Fn);

        r.SyncFailedIn(Fn, requestId, Reason, "raw");

        Assert.Equal(FunctionRuntimeClusterStatus.NotFound, Row(r).Status);
    }

    // The reconciler finding the image missing during a re-sync does not forget that it had been on the cluster.
    [Fact]
    public void Failed_OfAReSync_AfterTheReconcilerFoundItMissingAgain_RestsNotFound()
    {
        FunctionRuntime r = RuntimeIn(Seed.ReSyncWaitingSeen);
        r.MarkNotFoundIn(Fn);
        Guid requestId = Row(r).Operation.PendingId!.Value;

        r.SyncFailedIn(Fn, requestId, Reason, "raw");

        Assert.Equal(FunctionRuntimeClusterStatus.NotFound, Row(r).Status);
    }

    [Theory] // a report naming another request, or a cluster with nothing waiting, changes nothing
    [InlineData(Seed.FirstSyncWaiting)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    [InlineData(Seed.Available)]
    [InlineData(Seed.FirstSyncGaveUp)]
    [InlineData(Seed.ImageLost)]
    public void AReport_NamingAnotherSync_ReturnsFalse_AndChangesNothing(Seed seed)
    {
        FunctionRuntime r = RuntimeIn(seed);
        (FunctionRuntimeClusterStatus, FunctionRuntimeOperation) before = (Row(r).Status, Row(r).Operation);

        Assert.False(r.SyncSucceededIn(Fn, Guid.NewGuid()));
        Assert.False(r.SyncFailedIn(Fn, Guid.NewGuid(), Reason, "raw"));

        Assert.Equal(before, (Row(r).Status, Row(r).Operation));
    }

    [Fact] // a cluster with no row has nothing waiting: no row is added for it
    public void AReport_ForAClusterWithNoRow_ReturnsFalse_AndAddsNoRow()
    {
        FunctionRuntime r = NewRuntime();

        Assert.False(r.SyncSucceededIn(Fn, Guid.NewGuid()));
        Assert.False(r.SyncFailedIn(Fn, Guid.NewGuid(), Reason, "raw"));

        Assert.Empty(r.Clusters);
    }

    // ---------- observation: the status at rest, or only what is seen while a sync waits ----------

    [Theory]
    [InlineData(Seed.ObservedGone, FunctionRuntimeClusterStatus.Available)]
    [InlineData(Seed.FirstSyncGaveUp, FunctionRuntimeClusterStatus.Available)]
    [InlineData(Seed.Available, FunctionRuntimeClusterStatus.Available)]
    [InlineData(Seed.ImageLost, FunctionRuntimeClusterStatus.Available)]
    [InlineData(Seed.FirstSyncWaiting, FunctionRuntimeClusterStatus.Syncing)]
    [InlineData(Seed.ReSyncWaitingNotSeen, FunctionRuntimeClusterStatus.Syncing)]
    public void Observed_AtRestIsAvailable_WhileASyncWaitsOnlyRecordsItSeen(Seed seed, FunctionRuntimeClusterStatus after)
    {
        FunctionRuntime r = RuntimeIn(seed);
        Guid? waiting = Row(r).Operation.PendingId;
        string? reason = Row(r).Operation.FailureReason;

        r.MarkObservedIn(Fn);

        Assert.Equal(after, Row(r).Status);
        Assert.True(Row(r).IsAvailable);
        Assert.Equal(waiting, Row(r).Operation.PendingId);
        Assert.Equal(reason, Row(r).Operation.FailureReason);
    }

    [Theory]
    [InlineData(Seed.ObservedThere, FunctionRuntimeClusterStatus.NotFound)]
    [InlineData(Seed.Available, FunctionRuntimeClusterStatus.NotFound)]
    [InlineData(Seed.ImageLost, FunctionRuntimeClusterStatus.NotFound)]
    [InlineData(Seed.ObservedGone, FunctionRuntimeClusterStatus.NotFound)]
    [InlineData(Seed.FirstSyncGaveUp, FunctionRuntimeClusterStatus.Failed)]
    [InlineData(Seed.FirstSyncWaitingSeen, FunctionRuntimeClusterStatus.Syncing)]
    [InlineData(Seed.ReSyncWaitingSeen, FunctionRuntimeClusterStatus.Syncing)]
    public void NotFound_AtRestIsNotFound_ButFailedStays_WhileASyncWaitsOnlyRecordsItMissing(Seed seed,
        FunctionRuntimeClusterStatus after)
    {
        FunctionRuntime r = RuntimeIn(seed);
        Guid? waiting = Row(r).Operation.PendingId;

        r.MarkNotFoundIn(Fn);

        Assert.Equal(after, Row(r).Status);
        Assert.False(Row(r).IsAvailable);
        Assert.Equal(waiting, Row(r).Operation.PendingId);
    }

    [Fact]
    public void AnObservation_ForAClusterWithNoRow_AddsIt()
    {
        FunctionRuntime there = NewRuntime();
        FunctionRuntime gone = NewRuntime();

        there.MarkObservedIn(Fn);
        gone.MarkNotFoundIn(Fn);

        Assert.Equal(FunctionRuntimeClusterStatus.Available, Row(there).Status);
        Assert.Equal(FunctionRuntimeClusterStatus.NotFound, Row(gone).Status);
    }

    // ---------- what a deploy may use (the next iteration's gate) ----------
}

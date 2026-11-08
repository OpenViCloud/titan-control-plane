using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// The moves of a template in one cluster, one row of the table of titan-ops features/catalog/design.md#template-state-is-existence
// per test: a sync throws when it cannot be accepted, a report ends only the sync it names, an observation never throws
// (but for a cluster Titan holds no alias of) and ends nothing. The Seed enum names every situation a cluster can be in. How a sync ends, in more detail:
// VmTemplateSyncRequestLifecycleTests.
public class VmTemplateClusterStateMachineTests
{
    private const string Mock = "mock";
    private const string Alias = "tpl-a";
    private const string Reason = "Template sync failed due to a system error.";

    public enum Seed
    {
        ObservedGone,          // no row until the reconciler found no alias: NotFound with no alias
        FirstSyncWaiting,      // Syncing, no alias yet
        FirstSyncGaveUp,       // Failed
        Available,
        AliasLost,             // NotFound with the alias still recorded
        ReSyncWaitingNotSeen,  // Syncing, alias recorded, the reconciler did not find it
        ReSyncWaitingSeen,     // Syncing, alias recorded, the reconciler saw it
        AvailableWithAReason,  // a give-up that carried the alias
    }

    private static VmTemplate NewTemplate() => VmTemplate.Create("Ubuntu-Web", "desc", "Ubuntu-20.04", 20, "m");

    private static VmTemplate TemplateIn(Seed seed)
    {
        VmTemplate t = NewTemplate();
        switch (seed)
        {
            case Seed.ObservedGone: t.MarkNotFoundIn(Mock); break;
            case Seed.FirstSyncWaiting: t.SyncIn(Mock); break;
            case Seed.FirstSyncGaveUp: t.SyncFailedIn(Mock, t.SyncIn(Mock), null, Reason, "raw"); break;
            case Seed.Available: t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias); break;
            case Seed.AliasLost: t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias); t.MarkNotFoundIn(Mock); break;
            case Seed.ReSyncWaitingNotSeen: t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias); t.MarkNotFoundIn(Mock); t.SyncIn(Mock); break;
            case Seed.ReSyncWaitingSeen: t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias); t.MarkNotFoundIn(Mock); t.SyncIn(Mock); t.MarkObservedIn(Mock); break;
            case Seed.AvailableWithAReason: t.SyncFailedIn(Mock, t.SyncIn(Mock), Alias, Reason, "raw"); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed));
        }
        return t;
    }

    private static VmTemplateCluster Row(VmTemplate t) => t.Clusters.Single(c => c.ClusterId == Mock);

    // The status, then whether the cluster holds the alias as far as Titan last knew.
    private static string Shape(VmTemplate t)
    {
        VmTemplateCluster row = Row(t);
        string held = "-";
        if (row.InstanceStatus != null)
        {
            held = row.InstanceStatus.Value.ToString();
        }
        return $"{row.Status}/{held}";
    }

    private static bool Waits(Seed seed) =>
        seed is Seed.FirstSyncWaiting or Seed.ReSyncWaitingNotSeen or Seed.ReSyncWaitingSeen;

    // ---------- what each situation reads ----------

    [Theory]
    [InlineData(Seed.ObservedGone, "NotFound/-", false)]
    [InlineData(Seed.FirstSyncWaiting, "Syncing/-", false)]
    [InlineData(Seed.FirstSyncGaveUp, "Failed/-", false)]
    [InlineData(Seed.Available, "Available/Available", true)]
    [InlineData(Seed.AliasLost, "NotFound/-", false)]
    [InlineData(Seed.ReSyncWaitingNotSeen, "Syncing/-", false)]
    [InlineData(Seed.ReSyncWaitingSeen, "Syncing/Available", true)]
    [InlineData(Seed.AvailableWithAReason, "Available/Available", true)]
    public void EverySituation_ReadsItsStatus_AndWhetherTheClusterHoldsTheAlias(Seed seed, string shape, bool synced)
    {
        VmTemplate t = TemplateIn(seed);

        Assert.Equal(shape, Shape(t));
        Assert.Equal(synced, Row(t).IsSynced);
        Assert.Equal(synced, t.IsSyncedIn(Mock));
    }

    // ---------- Sync ----------

    [Theory] // from a cluster with no row, a Failed one and a NotFound one; Syncing and Available refuse
    [InlineData(Seed.ObservedGone, true)]
    [InlineData(Seed.FirstSyncGaveUp, true)]
    [InlineData(Seed.AliasLost, true)]
    [InlineData(Seed.FirstSyncWaiting, false)]
    [InlineData(Seed.ReSyncWaitingNotSeen, false)]
    [InlineData(Seed.ReSyncWaitingSeen, false)]
    [InlineData(Seed.Available, false)]
    [InlineData(Seed.AvailableWithAReason, false)]
    public void Sync_IsAcceptedFromFailedAndNotFound_AndRefusedFromSyncingAndAvailable(Seed seed, bool accepted)
    {
        VmTemplate t = TemplateIn(seed);
        string before = Shape(t);
        Guid? waitingBefore = Row(t).Operation.PendingId;

        if (accepted)
        {
            Guid request = t.SyncIn(Mock);

            Assert.Equal(request, Row(t).Operation.PendingId);
            Assert.Equal(VmTemplateClusterStatus.Syncing, Row(t).Status);
            Assert.Null(Row(t).Operation.LastSeenStatus);
        }
        else
        {
            BusinessException thrown = Assert.Throws<BusinessException>(() => t.SyncIn(Mock));

            Assert.Equal("INVALID_STATUS", thrown.Code);
            Assert.Equal(before, Shape(t));
            Assert.Equal(waitingBefore, Row(t).Operation.PendingId);
        }
    }

    [Fact] // the reason of the last give-up shows until a sync ends Done
    public void Sync_AfterAGiveUp_KeepsTheReason_UntilTheNextDone()
    {
        VmTemplate t = TemplateIn(Seed.FirstSyncGaveUp);

        Guid request = t.SyncIn(Mock);

        Assert.Equal(Reason, Row(t).Operation.FailureReason);
        t.SyncSucceededIn(Mock, request, Alias);
        Assert.Null(Row(t).Operation.FailureReason);
    }

    // ---------- SyncSucceeded ----------

    [Theory]
    [InlineData(Seed.FirstSyncWaiting)]
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void SyncSucceeded_OfTheWaitingSync_MakesTheClusterAvailable(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);

        bool ended = t.SyncSucceededIn(Mock, Row(t).Operation.PendingId!.Value, Alias);

        Assert.True(ended);
        Assert.Equal("Available/Available", Shape(t));
        Assert.Equal(VmTemplateOperation.None, Row(t).Operation);
        Assert.Equal(Alias, Row(t).ExternalTemplateId);
    }

    [Fact] // no alias stored and none in the report: refused before anything is written, the sync keeps waiting
    public void SyncSucceeded_WithNoAliasAtAll_Is422_AndWritesNothing()
    {
        VmTemplate t = TemplateIn(Seed.FirstSyncWaiting);
        Guid request = Row(t).Operation.PendingId!.Value;
        VmTemplateOperation before = Row(t).Operation;

        BusinessException thrown = Assert.Throws<BusinessException>(() => t.SyncSucceededIn(Mock, request, null));

        Assert.Equal("INVALID_STATUS", thrown.Code);
        Assert.Equal("Syncing/-", Shape(t));
        Assert.Equal(before, Row(t).Operation);
        Assert.Null(Row(t).ExternalTemplateId);
    }

    [Theory] // a re-sync of an alias already recorded needs no alias in the report
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void SyncSucceeded_OfAReSync_NeedsNoAliasInTheReport(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);

        Assert.True(t.SyncSucceededIn(Mock, Row(t).Operation.PendingId!.Value, null));

        Assert.Equal("Available/Available", Shape(t));
        Assert.Equal(Alias, Row(t).ExternalTemplateId);
    }

    [Fact]
    public void SyncSucceeded_ClearsTheReasonOfAnEarlierGiveUp()
    {
        VmTemplate t = TemplateIn(Seed.FirstSyncGaveUp);
        Guid request = t.SyncIn(Mock);

        t.SyncSucceededIn(Mock, request, Alias);

        Assert.Null(Row(t).Operation.FailureReason);
        Assert.Null(Row(t).Operation.FailureDetail);
        Assert.Null(Row(t).Operation.FailedAt);
    }

    // ---------- SyncFailed ----------

    [Fact] // before any alias: Failed, the reason recorded
    public void SyncFailed_WithNoAliasAnywhere_IsFailed()
    {
        VmTemplate t = TemplateIn(Seed.FirstSyncWaiting);

        bool ended = t.SyncFailedIn(Mock, Row(t).Operation.PendingId!.Value, null, Reason, "raw");

        Assert.True(ended);
        Assert.Equal("Failed/-", Shape(t));
        Assert.Equal((Reason, "raw"), (Row(t).Operation.FailureReason, Row(t).Operation.FailureDetail));
        Assert.Null(Row(t).Operation.PendingId);
    }

    [Theory] // the worker found the alias: the cluster holds it, the reason stays
    [InlineData(Seed.FirstSyncWaiting)]
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void SyncFailed_CarryingTheAlias_IsAvailable_AndKeepsTheReason(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);

        t.SyncFailedIn(Mock, Row(t).Operation.PendingId!.Value, Alias, Reason, "raw");

        Assert.Equal("Available/Available", Shape(t));
        Assert.Equal(Reason, Row(t).Operation.FailureReason);
        Assert.Equal(Alias, Row(t).ExternalTemplateId);
    }

    [Theory] // an alias recorded already: back to what the sync last saw
    [InlineData(Seed.ReSyncWaitingNotSeen, "NotFound/-")]
    [InlineData(Seed.ReSyncWaitingSeen, "Available/Available")]
    public void SyncFailed_OfAReSyncWithoutAnAliasInTheReport_ReturnsToWhatTheSyncSaw(Seed seed, string shape)
    {
        VmTemplate t = TemplateIn(seed);

        t.SyncFailedIn(Mock, Row(t).Operation.PendingId!.Value, null, Reason, "raw");

        Assert.Equal(shape, Shape(t));
        Assert.Equal(Reason, Row(t).Operation.FailureReason);
        Assert.Null(Row(t).Operation.LastSeenStatus);
    }

    // ---------- reports that name no waiting sync ----------

    [Theory]
    [InlineData(Seed.ObservedGone)]
    [InlineData(Seed.FirstSyncGaveUp)]
    [InlineData(Seed.Available)]
    [InlineData(Seed.AliasLost)]
    [InlineData(Seed.AvailableWithAReason)]
    public void AReport_WithNothingWaiting_IsIgnored_AndChangesNothing(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);
        string before = Shape(t);
        VmTemplateOperation operation = Row(t).Operation;

        Assert.False(t.SyncSucceededIn(Mock, Guid.NewGuid(), Alias));
        Assert.False(t.SyncFailedIn(Mock, Guid.NewGuid(), Alias, Reason, "raw"));

        Assert.Equal(before, Shape(t));
        Assert.Equal(operation, Row(t).Operation);
    }

    [Theory]
    [InlineData(Seed.FirstSyncWaiting)]
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void AReport_NamingAnotherSync_IsIgnored_AndTheSyncKeepsWaiting(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);
        string before = Shape(t);
        VmTemplateOperation operation = Row(t).Operation;

        Assert.False(t.SyncSucceededIn(Mock, Guid.NewGuid(), Alias));
        Assert.False(t.SyncFailedIn(Mock, Guid.NewGuid(), Alias, Reason, "raw"));

        Assert.Equal(before, Shape(t));
        Assert.Equal(operation, Row(t).Operation);
    }

    // ---------- MarkObserved ----------

    [Theory] // with no alias recorded there is nothing the reconciler could have looked for
    [InlineData(Seed.ObservedGone)]
    [InlineData(Seed.FirstSyncWaiting)]
    [InlineData(Seed.FirstSyncGaveUp)]
    public void MarkObserved_WithNoAliasRecorded_Is422_AndWritesNothing(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);
        string before = Shape(t);

        BusinessException thrown = Assert.Throws<BusinessException>(() => t.MarkObservedIn(Mock));

        Assert.Equal("PROVIDER_ID_MISSING", thrown.Code);
        Assert.Equal(before, Shape(t));
    }

    [Theory] // at rest the status follows what is seen
    [InlineData(Seed.Available)]
    [InlineData(Seed.AliasLost)]
    [InlineData(Seed.AvailableWithAReason)]
    public void MarkObserved_AtRest_MakesTheClusterAvailable(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);

        t.MarkObservedIn(Mock);

        Assert.Equal("Available/Available", Shape(t));
    }

    [Theory] // while a sync waits the status holds and only what was seen changes
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void MarkObserved_WhileASyncWaits_KeepsTheStatus_AndRecordsWhatWasSeen(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);
        Guid waiting = Row(t).Operation.PendingId!.Value;

        t.MarkObservedIn(Mock);

        Assert.Equal("Syncing/Available", Shape(t));
        Assert.Equal(waiting, Row(t).Operation.PendingId);
    }

    // ---------- MarkNotFound ----------

    [Theory] // at rest the status follows what is seen
    [InlineData(Seed.Available)]
    [InlineData(Seed.AvailableWithAReason)]
    [InlineData(Seed.AliasLost)]
    [InlineData(Seed.ObservedGone)]
    public void MarkNotFound_AtRest_MakesTheClusterNotFound(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);

        t.MarkNotFoundIn(Mock);

        Assert.Equal("NotFound/-", Shape(t));
    }

    [Theory] // while a sync waits the status holds and only what was seen changes
    [InlineData(Seed.FirstSyncWaiting)]
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void MarkNotFound_WhileASyncWaits_KeepsTheStatus_AndRecordsThatNothingWasSeen(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);
        Guid waiting = Row(t).Operation.PendingId!.Value;

        t.MarkNotFoundIn(Mock);

        Assert.Equal("Syncing/-", Shape(t));
        Assert.Equal(waiting, Row(t).Operation.PendingId);
    }

    [Fact] // a sync that gave up before any alias was there has no alias to lose
    public void MarkNotFound_OnAFailedCluster_IsIgnored()
    {
        VmTemplate t = TemplateIn(Seed.FirstSyncGaveUp);

        t.MarkNotFoundIn(Mock);

        Assert.Equal("Failed/-", Shape(t));
        Assert.Equal(Reason, Row(t).Operation.FailureReason);
    }

    [Fact] // observations converge: a cluster never asked is recorded as the reconciler found it, with no alias
    public void MarkNotFoundIn_AClusterWithNoRow_AddsARowThatIsNotFound()
    {
        VmTemplate t = NewTemplate();

        t.MarkNotFoundIn(Mock);

        Assert.Equal("NotFound/-", Shape(t));
        Assert.Null(Row(t).ExternalTemplateId);
        Assert.Null(Row(t).Operation.PendingId);
        Assert.Null(Row(t).Operation.FailureReason);
    }

    [Theory] // an observation never ends a sync nor clears a reason, from any situation
    [InlineData(Seed.Available)]
    [InlineData(Seed.AvailableWithAReason)]
    [InlineData(Seed.AliasLost)]
    [InlineData(Seed.FirstSyncGaveUp)]
    [InlineData(Seed.ReSyncWaitingNotSeen)]
    [InlineData(Seed.ReSyncWaitingSeen)]
    public void AnObservation_NeverClearsTheReason_NorEndsTheWaitingSync(Seed seed)
    {
        VmTemplate t = TemplateIn(seed);
        string? reason = Row(t).Operation.FailureReason;
        Guid? waiting = Row(t).Operation.PendingId;

        t.MarkNotFoundIn(Mock);
        if (Row(t).ExternalTemplateId != null)
        {
            t.MarkObservedIn(Mock);
        }

        Assert.Equal(reason, Row(t).Operation.FailureReason);
        Assert.Equal(waiting, Row(t).Operation.PendingId);
        Assert.Equal(Waits(seed), Row(t).Status == VmTemplateClusterStatus.Syncing);
    }
}

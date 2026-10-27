using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// A template is synced to every cluster on its own (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-cluster): each cluster holds
// whether that cluster has the image alias (existence — Incus has no status word for an alias,
// https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-state-is-existence) and a sync request (PendingIntentId). Sync is
// add-only, so Exists is the only thing anybody can want.
public class VmTemplateTests
{
    private const string Mock = "mock";
    private const string Singapore = "singapore01";

    private static string Shape(VmTemplate t, string clusterId)
    {
        VmTemplateCluster? cluster = t.ClusterOrNull(clusterId);
        if (cluster == null)
        {
            return "none";
        }
        string transition = "-";
        if (cluster.Transition != null)
        {
            transition = cluster.Transition.Value.ToString();
        }
        return $"{cluster.CurrentExistence}/{cluster.DesiredExistence}/{transition}";
    }

    private static VmTemplate NewTemplate() => VmTemplate.Create("Ubuntu-Web", "desc", "Ubuntu-20.04", 20, "m");

    private static VmTemplate SyncedIn(string clusterId, string externalTemplateId)
    {
        var t = NewTemplate();
        t.RequestSyncIn(clusterId);
        t.MarkCreatedIn(clusterId, externalTemplateId);
        return t;
    }

    // ── Create ────────────────────────────────────────────────────────────

    [Fact] // a fresh template is in no cluster: no customer sees it before a sync run has asked for it somewhere
    public void Create_IsInNoCluster_SoSeededTemplatesAreNotVisibleUntilSynced()
    {
        var t = NewTemplate();

        Assert.Empty(t.Clusters);
        Assert.NotEqual(Guid.Empty, t.Id);
        Assert.Equal("Ubuntu-Web", t.Name);
        Assert.Equal(20, t.RootDiskSizeGb);
        Assert.Equal("m", t.VmOfferingId);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-borrows-sizing — the template holds a POINTER to a tier, and the aggregate must not accept a
    // pointer to nothing. The FK covers ids that do not exist; it accepts "" and "   " happily,
    // and those only fail later, at sync time, far from the row that caused them.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithoutATier_IsRejectedAtTheAggregate_NotLeftToTheSyncPath(string tier)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            VmTemplate.Create("Ubuntu-Web", null, "Ubuntu-20.04", 20, tier));

        Assert.Equal("vmOfferingId", ex.ParamName);
    }

    // ── request ───────────────────────────────────────────────────────────

    [Fact]
    public void RequestSyncIn_ANewCluster_IsNotCreated_WithTheSyncWaiting()
    {
        var t = NewTemplate();

        t.RequestSyncIn(Mock);

        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
        Assert.NotNull(t.ClusterOrNull(Mock)!.PendingIntentId);
        Assert.Null(t.ClusterOrNull(Mock)!.ExternalTemplateId);
    }

    [Fact] // each cluster is its own request: asking in one cluster says nothing about another
    public void RequestSyncIn_OneCluster_LeavesTheOtherClusterAlone()
    {
        var t = SyncedIn(Mock, "tpl-a");

        t.RequestSyncIn(Singapore);

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Singapore));
    }

    [Fact] // a sync that gave up can be asked again; what was observed stays until the worker says otherwise
    public void RequestSyncIn_AfterAFailedSync_WaitsForTheWorker_AndKeepsTheReason()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);
        t.MarkRequestFailedIn(Mock, "wrong architecture", "raw");

        t.RequestSyncIn(Mock);

        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
        Assert.Equal("wrong architecture", t.ClusterOrNull(Mock)!.RequestFailureReason);
    }

    [Fact] // the alias went missing on Incus: sync pulls it again
    public void RequestSyncIn_AClusterWhoseAliasIsGone_IsAccepted()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);

        t.RequestSyncIn(Mock);

        Assert.Equal("NotFound/Exists/Syncing", Shape(t, Mock));
    }

    [Fact] // a repeat is refused; the handler recognises it first and only re-sends the command
    public void RequestSyncIn_WhileASyncWaitsInThatCluster_IsRefused()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);

        Assert.Throws<BadRequestException>(() => t.RequestSyncIn(Mock));
    }

    [Fact] // already there: nothing to ask for
    public void RequestSyncIn_AClusterThatHasIt_IsRefused()
    {
        var t = SyncedIn(Mock, "tpl-a");

        Assert.Throws<BadRequestException>(() => t.RequestSyncIn(Mock));
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RequestSyncIn_WithoutACluster_IsRefused(string clusterId)
    {
        var t = NewTemplate();

        Assert.Throws<ArgumentException>(() => t.RequestSyncIn(clusterId));
        Assert.Empty(t.Clusters);
    }

    // ── observations ──────────────────────────────────────────────────────

    [Fact]
    public void MarkCreatedIn_RecordsTheAlias_FulfilsTheSync_AndClearsAnyPriorFailure()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);
        t.MarkRequestFailedIn(Mock, "earlier error", "raw");
        t.RequestSyncIn(Mock);

        bool fulfilled = t.MarkCreatedIn(Mock, "tpl-a");

        Assert.True(fulfilled);
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Equal("tpl-a", t.ClusterOrNull(Mock)!.ExternalTemplateId);
        Assert.Null(t.ClusterOrNull(Mock)!.RequestFailureReason);
        Assert.Null(t.ClusterOrNull(Mock)!.RequestFailureDetail);
        Assert.Null(t.ClusterOrNull(Mock)!.RequestFailedAt);
    }

    [Fact] // a repeated report of the same command fulfils nothing the second time
    public void MarkCreatedIn_Repeated_FulfilsOnce()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);

        Assert.True(t.MarkCreatedIn(Mock, "tpl-a"));
        Assert.False(t.MarkCreatedIn(Mock, "tpl-a"));
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
    }

    [Fact] // the same template is its own alias on each cluster
    public void MarkCreatedIn_TwoClusters_KeepTheirOwnProviderIds()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);
        t.RequestSyncIn(Singapore);

        t.MarkCreatedIn(Mock, "tpl-a");
        t.MarkCreatedIn(Singapore, "tpl-b");

        Assert.Equal("tpl-a", t.ProviderIdIn(Mock));
        Assert.Equal("tpl-b", t.ProviderIdIn(Singapore));
    }

    [Fact]
    public void MarkCreatedIn_WithoutAnId_IsRefused()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);

        Assert.Throws<BadRequestException>(() => t.MarkCreatedIn(Mock, ""));
        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
    }

    [Fact] // observation converges: a report for a cluster Titan holds no row for is recorded, not refused
    public void MarkCreatedIn_AClusterNeverRequested_RecordsWhatWasObserved()
    {
        var t = NewTemplate();

        t.MarkCreatedIn(Singapore, "tpl-b");

        Assert.Equal("Exists/Exists/-", Shape(t, Singapore));
        Assert.Equal("tpl-b", t.ProviderIdIn(Singapore));
    }

    [Fact] // the sync gave up: nothing waits any more; existence stays what was last observed
    public void MarkRequestFailedIn_RecordsWhy_AndEndsTheWait()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);

        bool recorded = t.MarkRequestFailedIn(Mock, "image of another architecture", "raw error");

        Assert.True(recorded);
        Assert.Equal("NotCreated/Exists/-", Shape(t, Mock));
        VmTemplateCluster cluster = t.ClusterOrNull(Mock)!;
        Assert.Equal("image of another architecture", cluster.RequestFailureReason);
        Assert.Equal("raw error", cluster.RequestFailureDetail);
        Assert.NotNull(cluster.RequestFailedAt);
    }

    [Fact] // a give-up with no sync waiting is a stale report of a request already over
    public void MarkRequestFailedIn_WithNoSyncWaiting_IsStale_AndRecordsNothing()
    {
        var t = SyncedIn(Mock, "tpl-a");

        bool recorded = t.MarkRequestFailedIn(Mock, "late", "raw");

        Assert.False(recorded);
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Null(t.ClusterOrNull(Mock)!.RequestFailureReason);
    }

    [Fact] // the reconciler found the alias gone from one cluster: the other cluster is untouched
    public void MarkNotFoundIn_OnAClusterThatHadIt_KeepsItsProviderId_AndTheOtherCluster()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.RequestSyncIn(Singapore);
        t.MarkCreatedIn(Singapore, "tpl-b");

        t.MarkNotFoundIn(Mock);

        Assert.Equal("NotFound/Exists/-", Shape(t, Mock));
        Assert.Equal("tpl-a", t.ClusterOrNull(Mock)!.ExternalTemplateId);
        Assert.Equal("Exists/Exists/-", Shape(t, Singapore));
    }

    [Fact] // a missing alias does not erase why the last sync failed (the architecture reason stays readable)
    public void MarkNotFoundIn_KeepsTheReasonOfTheLastFailedSync()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);
        t.RequestSyncIn(Mock);
        t.MarkRequestFailedIn(Mock, "image of another architecture", "raw");

        t.MarkNotFoundIn(Mock);

        Assert.Equal("image of another architecture", t.ClusterOrNull(Mock)!.RequestFailureReason);
    }

    [Fact] // a sync still waiting ends once the reconciler sees the alias missing
    public void MarkNotFoundIn_EndsAWaitingSync()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);
        t.RequestSyncIn(Mock);

        t.MarkNotFoundIn(Mock);

        Assert.Equal("NotFound/Exists/-", Shape(t, Mock));
    }

    [Fact] // the reconciler sees an alias recorded lost: it is back, and any old failure no longer describes it
    public void MarkObservedIn_AfterALoss_RecordsItThereAgain()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);
        t.RequestSyncIn(Mock);
        t.MarkRequestFailedIn(Mock, "boom", "raw");

        t.MarkObservedIn(Mock);

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Null(t.ClusterOrNull(Mock)!.RequestFailureReason);
    }

    [Fact] // the reconciler sees the alias while a sync waits: that sync is done
    public void MarkObservedIn_FulfilsAWaitingSync()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);
        t.RequestSyncIn(Mock);

        t.MarkObservedIn(Mock);

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
    }

    [Fact] // seeing is only possible for a template Titan knows the alias of
    public void MarkObservedIn_WithNoProviderId_IsRefused()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);

        Assert.Throws<BadRequestException>(() => t.MarkObservedIn(Mock));
        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
    }

    // ── what a VM in a cluster can be created from ───────────────────────────

    [Fact] // a template on one cluster only does not exist for a VM in the other
    public void ProviderIdIn_AClusterNotCreated_IsRefused_AsNotSynced()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.RequestSyncIn(Singapore);

        Assert.Equal("TEMPLATE_NOT_SYNCED", Assert.Throws<BusinessException>(() => t.ProviderIdIn(Singapore)).Code);
        Assert.Equal("TEMPLATE_NOT_SYNCED", Assert.Throws<BusinessException>(() => t.ProviderIdIn("never-asked")).Code);
    }

    [Fact] // a template whose alias went missing keeps its provider id — it is still not usable
    public void ProviderIdIn_AClusterWhoseAliasIsGone_IsRefused_AsUnavailable()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);

        Assert.Equal("TEMPLATE_UNAVAILABLE", Assert.Throws<BusinessException>(() => t.ProviderIdIn(Mock)).Code);
    }

    [Fact]
    public void IsSyncedIn_IsTrue_OnlyForAClusterThatHasTheAlias()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.RequestSyncIn(Singapore);
        var lost = SyncedIn(Mock, "tpl-a");
        lost.MarkNotFoundIn(Mock);

        Assert.True(t.IsSyncedIn(Mock));
        Assert.False(t.IsSyncedIn(Singapore));
        Assert.False(t.IsSyncedIn("never-asked"));
        Assert.False(lost.IsSyncedIn(Mock));
    }
}

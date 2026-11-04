using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// A template is synced to every cluster on its own (titan-ops features/catalog/design.md#template-per-cluster): each cluster holds
// whether that cluster has the image alias (existence — Incus has no status word for an alias,
// titan-ops features/catalog/design.md#template-state-is-existence) and a sync request (Pending). Sync is add-only, so
// Exists is the only thing anybody can want. How a sync ends: VmTemplateSyncRequestLifecycleTests.
public class VmTemplateTests
{
    private const string Mock = "mock";
    private const string Singapore = "singapore01";

    private static string Shape(VmTemplate t, string clusterId)
    {
        VmTemplateCluster? cluster = t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == clusterId);
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
        t.EndSyncRequest(clusterId, t.RequestSyncIn(clusterId).Id, VmTemplateRequestEnd.Done(externalTemplateId));
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

    // titan-ops features/catalog/design.md#template-borrows-sizing — the template holds a POINTER to a tier, and the aggregate must not accept a
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
        Assert.Equal(VmTemplateRequestKind.Sync, t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.Pending!.Kind);
        Assert.Null(t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.ExternalTemplateId);
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
        t.EndSyncRequest(Mock, t.RequestSyncIn(Mock).Id, VmTemplateRequestEnd.Failed(null, "wrong architecture", "raw"));

        t.RequestSyncIn(Mock);

        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
        Assert.Equal("wrong architecture", t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.RequestFailureReason);
    }

    [Fact] // the alias went missing on Incus: sync pulls it again
    public void RequestSyncIn_AClusterWhoseAliasIsGone_IsAccepted()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);

        t.RequestSyncIn(Mock);

        Assert.Equal("NotFound/Exists/Syncing", Shape(t, Mock));
    }

    [Fact] // a repeat is refused; the handler recognises it first and sends nothing
    public void RequestSyncIn_WhileASyncWaitsInThatCluster_IsRefused()
    {
        var t = NewTemplate();
        t.RequestSyncIn(Mock);

        Assert.Throws<BusinessException>(() => t.RequestSyncIn(Mock));
    }

    [Fact] // already there: nothing to ask for
    public void RequestSyncIn_AClusterThatHasIt_IsRefused()
    {
        var t = SyncedIn(Mock, "tpl-a");

        Assert.Throws<BusinessException>(() => t.RequestSyncIn(Mock));
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

    [Fact] // the same template is its own alias on each cluster
    public void EachCluster_KeepsItsOwnProviderId()
    {
        var t = NewTemplate();
        PendingRequest<VmTemplateRequestKind> inMock = t.RequestSyncIn(Mock);
        PendingRequest<VmTemplateRequestKind> inSingapore = t.RequestSyncIn(Singapore);

        t.EndSyncRequest(Mock, inMock.Id, VmTemplateRequestEnd.Done("tpl-a"));
        t.EndSyncRequest(Singapore, inSingapore.Id, VmTemplateRequestEnd.Done("tpl-b"));

        Assert.Equal("tpl-a", t.ProviderIdIn(Mock));
        Assert.Equal("tpl-b", t.ProviderIdIn(Singapore));
    }

    [Fact] // the reconciler found the alias gone from one cluster: the other cluster is untouched
    public void MarkNotFoundIn_OnAClusterThatHadIt_KeepsItsProviderId_AndTheOtherCluster()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.EndSyncRequest(Singapore, t.RequestSyncIn(Singapore).Id, VmTemplateRequestEnd.Done("tpl-b"));

        t.MarkNotFoundIn(Mock);

        Assert.Equal("NotFound/Exists/-", Shape(t, Mock));
        Assert.Equal("tpl-a", t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.ExternalTemplateId);
        Assert.Equal("Exists/Exists/-", Shape(t, Singapore));
    }

    [Fact] // a missing alias does not erase why the last sync failed (the architecture reason stays readable)
    public void MarkNotFoundIn_KeepsTheReasonOfTheLastFailedSync()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);
        t.EndSyncRequest(Mock, t.RequestSyncIn(Mock).Id, VmTemplateRequestEnd.Failed(null, "image of another architecture", "raw"));

        t.MarkNotFoundIn(Mock);

        Assert.Equal("image of another architecture", t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.RequestFailureReason);
    }

    [Fact] // the reconciler sees an alias recorded lost: it is back
    public void MarkObservedIn_AfterALoss_RecordsItThereAgain()
    {
        var t = SyncedIn(Mock, "tpl-a");
        t.MarkNotFoundIn(Mock);

        t.MarkObservedIn(Mock);

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.True(t.IsSyncedIn(Mock));
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

using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a template in one cluster: one waiting sync, ended only by
// the report carrying its id; an observation writes the status at rest or what is seen while the sync waits and ends
// nothing; Done and Failed have their effect (titan-ops features/catalog/design.md#template-state-is-existence). The
// template is never Deleted, so nothing absorbs a report: the id check alone decides. VmTemplateTests keeps what a request
// may be made from; this file is about how a sync ends.
public class VmTemplateSyncRequestLifecycleTests
{
    private const string Mock = "mock";
    private const string Singapore = "singapore01";
    private const string Alias = "tpl-a";

    private static VmTemplate NewTemplate() => VmTemplate.Create("Ubuntu-Web", "desc", "Ubuntu-20.04", 20, "m");

    private static VmTemplateCluster Row(VmTemplate t, string clusterId) => t.Clusters.Single(c => c.ClusterId == clusterId);

    // The status, then whether the cluster holds the alias as far as Titan last knew.
    private static string Shape(VmTemplate t, string clusterId)
    {
        VmTemplateCluster row = Row(t, clusterId);
        string held = "-";
        if (row.InstanceStatus != null)
        {
            held = row.InstanceStatus.Value.ToString();
        }
        return $"{row.Status}/{held}";
    }

    private static bool Failed(VmTemplate t, string clusterId, Guid operationId, string? alias = null) =>
        t.SyncFailedIn(clusterId, operationId, alias, "Template sync failed due to a system error.", "raw");

    // ---------- the request ----------

    [Fact]
    public void ASync_WaitsWithAnIdAndATime_AndReadsSyncing()
    {
        VmTemplate t = NewTemplate();

        Guid request = t.SyncIn(Mock);

        Assert.Equal(request, Row(t, Mock).Operation.PendingId);
        Assert.NotEqual(Guid.Empty, request);
        Assert.True(Row(t, Mock).Operation.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal("Syncing/-", Shape(t, Mock));
    }

    [Fact] // each ask is a new request: the id of a sync that ended is never asked again
    public void ASyncAskedAgainAfterOneFailed_IsANewRequest()
    {
        VmTemplate t = NewTemplate();
        Guid first = t.SyncIn(Mock);
        Failed(t, Mock, first);

        Guid second = t.SyncIn(Mock);

        Assert.NotEqual(first, second);
    }

    // ---------- Done ----------

    [Fact]
    public void Done_RecordsTheAlias_Available_AndClearsAnEarlierFailure()
    {
        VmTemplate t = NewTemplate();
        Failed(t, Mock, t.SyncIn(Mock));
        Guid again = t.SyncIn(Mock);

        bool ended = t.SyncSucceededIn(Mock, again, Alias);

        Assert.True(ended);
        Assert.Equal("Available/Available", Shape(t, Mock));
        VmTemplateCluster row = Row(t, Mock);
        Assert.Equal((Alias, (string?)null, (string?)null, (DateTimeOffset?)null),
            (row.ExternalTemplateId, row.Operation.FailureReason, row.Operation.FailureDetail, row.Operation.FailedAt));
        Assert.Null(row.Operation.PendingId);
    }

    [Fact] // a re-sync of an alias Incus lost names the same alias; the first one recorded stays
    public void Done_OfAReSync_KeepsTheFirstAlias()
    {
        VmTemplate t = NewTemplate();
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);
        t.MarkNotFoundIn(Mock);
        Guid resync = t.SyncIn(Mock);

        t.SyncSucceededIn(Mock, resync, "tpl-other");

        Assert.Equal(Alias, Row(t, Mock).ExternalTemplateId);
        Assert.Equal("Available/Available", Shape(t, Mock));
    }

    [Fact] // the report of a sync already ended, sent again after its answer was lost
    public void Done_Repeated_IsIgnored_TheSecondTime()
    {
        VmTemplate t = NewTemplate();
        Guid sync = t.SyncIn(Mock);
        t.SyncSucceededIn(Mock, sync, Alias);

        Assert.False(t.SyncSucceededIn(Mock, sync, Alias));
        Assert.Equal("Available/Available", Shape(t, Mock));
    }

    // ---------- Failed ----------

    [Fact]
    public void Failed_BeforeAnyAlias_RecordsTheReasonAndDetail_AndTheClusterIsFailed()
    {
        VmTemplate t = NewTemplate();
        Guid sync = t.SyncIn(Mock);

        bool ended = Failed(t, Mock, sync);

        Assert.True(ended);
        Assert.Equal("Failed/-", Shape(t, Mock));
        VmTemplateCluster row = Row(t, Mock);
        Assert.Equal(("Template sync failed due to a system error.", "raw"), (row.Operation.FailureReason, row.Operation.FailureDetail));
        Assert.NotNull(row.Operation.FailedAt);
        Assert.Null(row.ExternalTemplateId);
    }

    // A give-up that found the alias of the cluster's architecture on Incus: the template is there, as a create that
    // gave up after Incus made the resource (titan-ops architecture/resource-lifecycle.md#kinds). The reason stays.
    [Fact]
    public void Failed_CarryingTheAlias_RecordsItAndIsAvailable_AndKeepsTheReason()
    {
        VmTemplate t = NewTemplate();
        Guid sync = t.SyncIn(Mock);

        Failed(t, Mock, sync, Alias);

        Assert.Equal("Available/Available", Shape(t, Mock));
        Assert.Equal((Alias, "Template sync failed due to a system error."), (Row(t, Mock).ExternalTemplateId, Row(t, Mock).Operation.FailureReason));
        Assert.Equal(Alias, t.ProviderIdIn(Mock));
    }

    // ---------- reports that end nothing ----------

    [Fact] // a give-up resent after a new sync was asked names the old request
    public void AReportNamingAnotherRequest_ChangesNothing()
    {
        VmTemplate t = NewTemplate();
        Guid sync = t.SyncIn(Mock);

        Assert.False(Failed(t, Mock, Guid.NewGuid()));

        Assert.Equal(sync, Row(t, Mock).Operation.PendingId);
        Assert.Equal("Syncing/-", Shape(t, Mock));
        Assert.Null(Row(t, Mock).Operation.FailureReason);
    }

    [Fact]
    public void AReportWithNothingWaiting_ChangesNothing()
    {
        VmTemplate t = NewTemplate();
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);

        Assert.False(Failed(t, Mock, Guid.NewGuid()));

        Assert.Equal("Available/Available", Shape(t, Mock));
        Assert.Null(Row(t, Mock).Operation.FailureReason);
    }

    [Fact] // a report for a cluster never asked has nothing to end, and invents no row for it
    public void AReportForAClusterWithNoRow_EndsNothing_AndAddsNoRow()
    {
        VmTemplate t = NewTemplate();
        t.SyncIn(Mock);

        Assert.False(t.SyncSucceededIn(Singapore, Guid.NewGuid(), Alias));
        Assert.False(Failed(t, Singapore, Guid.NewGuid()));

        Assert.Equal([Mock], t.Clusters.Select(c => c.ClusterId));
    }

    [Fact] // the report of one cluster's sync does not end the other cluster's
    public void TheReportOfOneCluster_LeavesTheOtherClustersSyncWaiting()
    {
        VmTemplate t = NewTemplate();
        Guid inMock = t.SyncIn(Mock);
        Guid inSingapore = t.SyncIn(Singapore);

        Assert.False(t.SyncSucceededIn(Singapore, inMock, Alias));
        t.SyncSucceededIn(Mock, inMock, Alias);

        Assert.Equal("Available/Available", Shape(t, Mock));
        Assert.Equal(inSingapore, Row(t, Singapore).Operation.PendingId);
    }

    // ---------- observations: current only ----------

    [Fact] // the reconciler sees the alias back while a re-sync waits: the sync still waits for its own report
    public void SeeingTheAlias_WhileASyncWaits_IsSeenAndEndsNoSync()
    {
        VmTemplate t = NewTemplate();
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);
        t.MarkNotFoundIn(Mock);
        Guid resync = t.SyncIn(Mock);

        t.MarkObservedIn(Mock);

        Assert.Equal("Syncing/Available", Shape(t, Mock));
        Assert.Equal(resync, Row(t, Mock).Operation.PendingId);
    }

    [Fact] // a re-sync the reconciler races: the alias still gone ends nothing, and the sync's Done ends it after
    public void NotSeeingTheAlias_WhileASyncWaits_IsSeenAndEndsNoSync()
    {
        VmTemplate t = NewTemplate();
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);
        t.MarkNotFoundIn(Mock);
        Guid resync = t.SyncIn(Mock);

        t.MarkNotFoundIn(Mock);

        Assert.Equal("Syncing/-", Shape(t, Mock));
        t.SyncSucceededIn(Mock, resync, Alias);
        Assert.Equal("Available/Available", Shape(t, Mock));
    }

    [Fact] // an observation writes current, not the request's failure: only the next Done clears the reason
    public void SeeingTheAlias_KeepsTheReasonOfTheLastFailedSync()
    {
        VmTemplate t = NewTemplate();
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);
        t.MarkNotFoundIn(Mock);
        Failed(t, Mock, t.SyncIn(Mock));

        t.MarkObservedIn(Mock);

        Assert.Equal("Available/Available", Shape(t, Mock));
        Assert.Equal("Template sync failed due to a system error.", Row(t, Mock).Operation.FailureReason);
    }

    [Fact] // seeing is only possible for a template Titan knows the alias of
    public void SeeingTheAlias_WithNoProviderId_IsRefused()
    {
        VmTemplate t = NewTemplate();
        t.SyncIn(Mock);

        Assert.Equal("PROVIDER_ID_MISSING", Assert.Throws<BusinessException>(() => t.MarkObservedIn(Mock)).Code);
        Assert.Equal("Syncing/-", Shape(t, Mock));
    }
}

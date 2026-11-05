using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a template in one cluster: one waiting sync, ended only by
// the report carrying its id; an observation writes current and ends nothing; Done and Failed have their effect
// (titan-ops architecture/resource-lifecycle.md#kinds). The template is never Deleted, so nothing absorbs a report: the id
// check alone decides. VmTemplateTests keeps what a request may be made from; this file is about how a sync ends.
public class VmTemplateSyncRequestLifecycleTests
{
    private const string Mock = "mock";
    private const string Singapore = "singapore01";
    private const string Alias = "tpl-a";

    private static VmTemplate NewTemplate() => VmTemplate.Create("Ubuntu-Web", "desc", "Ubuntu-20.04", 20, "m");

    private static VmTemplateCluster Row(VmTemplate t, string clusterId) => t.Clusters.Single(c => c.ClusterId == clusterId);

    private static string Shape(VmTemplate t, string clusterId)
    {
        VmTemplateCluster row = Row(t, clusterId);
        return $"{row.CurrentExistence}/{row.DesiredExistence}/{(row.Transition == null ? "-" : row.Transition.Value.ToString())}";
    }

    private static PendingOperation<VmTemplateOperationKind>? Failed(VmTemplate t, string clusterId, Guid operationId,
        string? alias = null) =>
        t.FailSyncOperation(clusterId, operationId, alias, "Template sync failed due to a system error.", "raw");

    // ---------- the request ----------

    [Fact]
    public void ASync_WaitsWithAnIdAKindAndATime_AndReadsSyncing()
    {
        VmTemplate t = NewTemplate();

        PendingOperation<VmTemplateOperationKind> request = t.SyncIn(Mock);

        Assert.Equal(request, Row(t, Mock).Pending);
        Assert.Equal(VmTemplateOperationKind.Sync, request.Kind);
        Assert.NotEqual(Guid.Empty, request.Id);
        Assert.True(request.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
    }

    [Fact] // each ask is a new request: the id of a sync that ended is never asked again
    public void ASyncAskedAgainAfterOneFailed_IsANewRequest()
    {
        VmTemplate t = NewTemplate();
        PendingOperation<VmTemplateOperationKind> first = t.SyncIn(Mock);
        Failed(t, Mock, first.Id);

        PendingOperation<VmTemplateOperationKind> second = t.SyncIn(Mock);

        Assert.NotEqual(first.Id, second.Id);
    }

    // ---------- Done ----------

    [Fact]
    public void Done_RecordsTheAlias_Exists_AndClearsAnEarlierFailure()
    {
        VmTemplate t = NewTemplate();
        Failed(t, Mock, t.SyncIn(Mock).Id);
        PendingOperation<VmTemplateOperationKind> again = t.SyncIn(Mock);

        PendingOperation<VmTemplateOperationKind>? ended = t.FinishSyncOperation(Mock, again.Id, Alias);

        Assert.Equal(again, ended);
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        VmTemplateCluster row = Row(t, Mock);
        Assert.Equal((Alias, (string?)null, (string?)null, (DateTimeOffset?)null),
            (row.ExternalTemplateId, row.OperationFailureReason, row.OperationFailureDetail, row.OperationFailedAt));
        Assert.Null(row.Pending);
    }

    [Fact] // a re-sync of an alias Incus lost names the same alias; the first one recorded stays
    public void Done_OfAReSync_KeepsTheFirstAlias()
    {
        VmTemplate t = NewTemplate();
        t.FinishSyncOperation(Mock, t.SyncIn(Mock).Id, Alias);
        t.MarkNotFoundIn(Mock);
        PendingOperation<VmTemplateOperationKind> resync = t.SyncIn(Mock);

        t.FinishSyncOperation(Mock, resync.Id, "tpl-other");

        Assert.Equal(Alias, Row(t, Mock).ExternalTemplateId);
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
    }

    [Fact] // the report of a sync already ended, sent again after its answer was lost
    public void Done_Repeated_IsIgnored_TheSecondTime()
    {
        VmTemplate t = NewTemplate();
        PendingOperation<VmTemplateOperationKind> sync = t.SyncIn(Mock);
        t.FinishSyncOperation(Mock, sync.Id, Alias);

        Assert.Null(t.FinishSyncOperation(Mock, sync.Id, Alias));
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
    }

    // ---------- Failed ----------

    [Fact]
    public void Failed_RecordsTheReasonAndDetail_AndKeepsTheExistence()
    {
        VmTemplate t = NewTemplate();
        PendingOperation<VmTemplateOperationKind> sync = t.SyncIn(Mock);

        PendingOperation<VmTemplateOperationKind>? ended = Failed(t, Mock, sync.Id);

        Assert.Equal(sync, ended);
        Assert.Equal("NotCreated/Exists/-", Shape(t, Mock));
        VmTemplateCluster row = Row(t, Mock);
        Assert.Equal(("Template sync failed due to a system error.", "raw"), (row.OperationFailureReason, row.OperationFailureDetail));
        Assert.NotNull(row.OperationFailedAt);
        Assert.Null(row.ExternalTemplateId);
    }

    // A give-up that found the alias of the cluster's architecture on Incus: the template is there, as a create that
    // gave up after Incus made the resource (titan-ops architecture/resource-lifecycle.md#kinds). The reason stays.
    [Fact]
    public void Failed_CarryingTheAlias_RecordsItAndExists_AndKeepsTheReason()
    {
        VmTemplate t = NewTemplate();
        PendingOperation<VmTemplateOperationKind> sync = t.SyncIn(Mock);

        Failed(t, Mock, sync.Id, Alias);

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Equal((Alias, "Template sync failed due to a system error."), (Row(t, Mock).ExternalTemplateId, Row(t, Mock).OperationFailureReason));
        Assert.Equal(Alias, t.ProviderIdIn(Mock));
    }

    // ---------- reports that end nothing ----------

    [Fact] // a give-up resent after a new sync was asked names the old request
    public void AReportNamingAnotherRequest_ChangesNothing()
    {
        VmTemplate t = NewTemplate();
        PendingOperation<VmTemplateOperationKind> sync = t.SyncIn(Mock);

        Assert.Null(Failed(t, Mock, Guid.NewGuid()));

        Assert.Equal(sync, Row(t, Mock).Pending);
        Assert.Null(Row(t, Mock).OperationFailureReason);
    }

    [Fact]
    public void AReportWithNothingWaiting_ChangesNothing()
    {
        VmTemplate t = NewTemplate();
        t.FinishSyncOperation(Mock, t.SyncIn(Mock).Id, Alias);

        Assert.Null(Failed(t, Mock, Guid.NewGuid()));

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Null(Row(t, Mock).OperationFailureReason);
    }

    [Fact] // a report for a cluster never asked has nothing to end, and invents no row for it
    public void AReportForAClusterWithNoRow_EndsNothing_AndAddsNoRow()
    {
        VmTemplate t = NewTemplate();
        t.SyncIn(Mock);

        Assert.Null(t.FinishSyncOperation(Singapore, Guid.NewGuid(), Alias));

        Assert.Equal([Mock], t.Clusters.Select(c => c.ClusterId));
    }

    [Fact] // the report of one cluster's sync does not end the other cluster's
    public void TheReportOfOneCluster_LeavesTheOtherClustersSyncWaiting()
    {
        VmTemplate t = NewTemplate();
        PendingOperation<VmTemplateOperationKind> inMock = t.SyncIn(Mock);
        PendingOperation<VmTemplateOperationKind> inSingapore = t.SyncIn(Singapore);

        Assert.Null(t.FinishSyncOperation(Singapore, inMock.Id, Alias));
        t.FinishSyncOperation(Mock, inMock.Id, Alias);

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Equal(inSingapore, Row(t, Singapore).Pending);
    }

    // ---------- observations: current only ----------

    [Fact] // the reconciler sees the alias back while a re-sync waits: the sync still waits for its own report
    public void SeeingTheAlias_WritesExists_AndEndsNoSync()
    {
        VmTemplate t = NewTemplate();
        t.FinishSyncOperation(Mock, t.SyncIn(Mock).Id, Alias);
        t.MarkNotFoundIn(Mock);
        PendingOperation<VmTemplateOperationKind> resync = t.SyncIn(Mock);

        t.MarkObservedIn(Mock);

        Assert.Equal("Exists/Exists/Syncing", Shape(t, Mock));
        Assert.Equal(resync, Row(t, Mock).Pending);
    }

    [Fact] // a re-sync the reconciler races: the alias still gone ends nothing, and the sync's Done ends it after
    public void NotSeeingTheAlias_WritesNotFound_AndEndsNoSync()
    {
        VmTemplate t = NewTemplate();
        t.FinishSyncOperation(Mock, t.SyncIn(Mock).Id, Alias);
        t.MarkNotFoundIn(Mock);
        PendingOperation<VmTemplateOperationKind> resync = t.SyncIn(Mock);

        t.MarkNotFoundIn(Mock);

        Assert.Equal("NotFound/Exists/Syncing", Shape(t, Mock));
        t.FinishSyncOperation(Mock, resync.Id, Alias);
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
    }

    [Fact] // an observation writes current, not the request's failure: only the next Done clears the reason
    public void SeeingTheAlias_KeepsTheReasonOfTheLastFailedSync()
    {
        VmTemplate t = NewTemplate();
        t.FinishSyncOperation(Mock, t.SyncIn(Mock).Id, Alias);
        t.MarkNotFoundIn(Mock);
        Failed(t, Mock, t.SyncIn(Mock).Id);

        t.MarkObservedIn(Mock);

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Equal("Template sync failed due to a system error.", Row(t, Mock).OperationFailureReason);
    }

    [Fact] // seeing is only possible for a template Titan knows the alias of
    public void SeeingTheAlias_WithNoProviderId_IsRefused()
    {
        VmTemplate t = NewTemplate();
        t.SyncIn(Mock);

        Assert.Equal("PROVIDER_ID_MISSING", Assert.Throws<BusinessException>(() => t.MarkObservedIn(Mock)).Code);
        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
    }
}

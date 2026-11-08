using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The request routes of titan-ops architecture/resource-lifecycle.md#callback-routes for a template in one cluster, at the
// handler: a report ends the sync it names; an observation writes the status at rest or what is seen and ends nothing; the template's row is
// locked (the fake finds a template only through the locked read). TemplateServiceTests keeps sync-all and what customers see.
public class TemplateRequestHandlerTests
{
    private const string Mock = "mock", Singapore = "singapore01";
    private const string Alias = "tpl-205";
    private const string Reason = "Template sync failed due to a system error.";

    private static readonly ClusterOptions Clusters = new()
    {
        Clusters = [new ClusterConfig { Id = Mock, Name = "Mock" }, new ClusterConfig { Id = Singapore, Name = "Singapore 01" }],
    };

    private static VmTemplate Waiting(out Guid requestId)
    {
        var t = VmTemplate.Create("Ubuntu", null, "img", 10, "s");
        requestId = t.SyncIn(Mock);
        return t;
    }

    private static VmTemplateCluster Row(VmTemplate t, string clusterId) => t.Clusters.Single(c => c.ClusterId == clusterId);

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

    private static Task End(FakeVmTemplateRepository repo, Guid templateId, Guid requestId, string outcome,
        string? externalId = null, string? reason = null, string clusterId = Mock) =>
        new EndTemplateSyncRequestCommandHandler(repo, Clusters).HandleAsync(
            new EndTemplateSyncRequestCommand(templateId, clusterId, requestId, outcome, externalId, reason, "raw"));

    private static Task Observe(FakeVmTemplateRepository repo, Guid templateId, bool exists, string clusterId = Mock) =>
        new ObserveTemplateCommandHandler(repo, Clusters).HandleAsync(new ObserveTemplateCommand(templateId, clusterId, exists));

    // ---------- the report of the sync's own command ----------

    [Fact]
    public async Task Done_NamingTheWaitingSync_RecordsTheAlias_AndSaves()
    {
        VmTemplate t = Waiting(out Guid requestId);
        var repo = new FakeVmTemplateRepository(t);

        await End(repo, t.Id, requestId, "Done", Alias);

        Assert.Equal("Available/Available", Shape(t, Mock));
        Assert.Equal(Alias, t.ProviderIdIn(Mock));
        Assert.True(repo.Saved);
        Assert.Equal([t.Id], repo.Locked);
    }

    [Fact]
    public async Task Failed_RecordsTheReasonAndDetail_AndNothingWaits()
    {
        VmTemplate t = Waiting(out Guid requestId);
        var repo = new FakeVmTemplateRepository(t);

        await End(repo, t.Id, requestId, "Failed", reason: Reason);

        Assert.Equal("Failed/-", Shape(t, Mock));
        Assert.Equal((Reason, "raw"), (Row(t, Mock).Operation.FailureReason, Row(t, Mock).Operation.FailureDetail));
        Assert.True(repo.Saved);
    }

    [Fact] // a give-up that found the alias of the cluster's architecture: the template is there
    public async Task Failed_CarryingTheAlias_RecordsItAndIsAvailable_AndKeepsTheReason()
    {
        VmTemplate t = Waiting(out Guid requestId);
        var repo = new FakeVmTemplateRepository(t);

        await End(repo, t.Id, requestId, "Failed", Alias, Reason);

        Assert.Equal("Available/Available", Shape(t, Mock));
        Assert.Equal((Alias, Reason), (Row(t, Mock).ExternalTemplateId, Row(t, Mock).Operation.FailureReason));
    }

    [Fact] // 204 at the endpoint: the worker has nothing to retry
    public async Task AReportNamingAnotherRequest_ChangesNothing_AndSavesNothing()
    {
        VmTemplate t = Waiting(out Guid requestId);
        var repo = new FakeVmTemplateRepository(t);

        await End(repo, t.Id, Guid.NewGuid(), "Done", Alias);

        Assert.Equal(requestId, Row(t, Mock).Operation.PendingId);
        Assert.False(repo.Saved);
    }

    [Fact] // the other cluster's sync is not this report's
    public async Task AReportOfOneCluster_LeavesTheOtherClusterAlone()
    {
        VmTemplate t = Waiting(out Guid inMock);
        Guid inSingapore = t.SyncIn(Singapore);
        var repo = new FakeVmTemplateRepository(t);

        await End(repo, t.Id, inSingapore, "Done", Alias, clusterId: Singapore);

        Assert.Equal(("Syncing/-", "Available/Available"), (Shape(t, Mock), Shape(t, Singapore)));
        Assert.Equal(inMock, Row(t, Mock).Operation.PendingId);
    }

    // The alias is what a VM is created from: a Done that does not name it, on a cluster that has none, would leave the
    // template Available and unusable.
    [Fact]
    public async Task Done_WithoutTheAlias_WhenTheClusterHasNone_Is422_AndTheSyncKeepsWaiting()
    {
        VmTemplate t = Waiting(out Guid requestId);
        var repo = new FakeVmTemplateRepository(t);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => End(repo, t.Id, requestId, "Done"));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(requestId, Row(t, Mock).Operation.PendingId);
        Assert.False(repo.Saved);
    }

    [Fact] // a re-sync of an alias Incus lost: the alias is already recorded
    public async Task Done_WithoutTheAlias_OfAReSync_EndsIt()
    {
        var t = VmTemplate.Create("Ubuntu", null, "img", 10, "s");
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);
        t.MarkNotFoundIn(Mock);
        Guid resync = t.SyncIn(Mock);
        var repo = new FakeVmTemplateRepository(t);

        await End(repo, t.Id, resync, "Done");

        Assert.Equal("Available/Available", Shape(t, Mock));
    }

    [Theory] // a malformed report is the worker's bug, whatever the template: refused before anything is looked up
    [InlineData("Maybe")]
    [InlineData("Created")]
    [InlineData("")]
    public async Task AnOutcomeTheControlPlaneCannotRead_Is400_BeforeTheTemplateIsLookedUp(string outcome)
    {
        var repo = new FakeVmTemplateRepository();

        var ex = await Assert.ThrowsAsync<BadRequestException>(() => End(repo, Guid.NewGuid(), Guid.NewGuid(), outcome, Alias));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Empty(repo.Locked);
    }

    [Fact] // a report for a cluster Titan does not have must not invent a row for it
    public async Task AReportForAnUnknownCluster_Is404ClusterNotFound_AndTouchesNoTemplate()
    {
        VmTemplate t = Waiting(out Guid requestId);
        var repo = new FakeVmTemplateRepository(t);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            End(repo, t.Id, requestId, "Done", Alias, clusterId: "vn-hcm-1"));

        Assert.Equal("CLUSTER_NOT_FOUND", ex.Code);
        Assert.Empty(repo.Locked);
    }

    [Fact]
    public async Task AReportForNoTemplate_Is404TemplateNotFound()
    {
        var repo = new FakeVmTemplateRepository();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => End(repo, Guid.NewGuid(), Guid.NewGuid(), "Done", Alias));

        Assert.Equal("TEMPLATE_NOT_FOUND", ex.Code);
    }

    // ---------- the reconciler's observation ----------

    [Fact] // the alias is back while a re-sync waits: the sync waits for its own report
    public async Task SeeingTheAlias_IsSeenOnTheSync_AndLeavesTheSyncWaiting()
    {
        var t = VmTemplate.Create("Ubuntu", null, "img", 10, "s");
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);
        t.MarkNotFoundIn(Mock);
        Guid resync = t.SyncIn(Mock);
        var repo = new FakeVmTemplateRepository(t);

        await Observe(repo, t.Id, exists: true);

        Assert.Equal("Syncing/Available", Shape(t, Mock));
        Assert.Equal(resync, Row(t, Mock).Operation.PendingId);
        Assert.True(repo.Saved);
        Assert.Equal([t.Id], repo.Locked);
    }

    [Fact]
    public async Task NotSeeingTheAlias_WritesNotFound_AndHidesItFromCustomers()
    {
        var t = VmTemplate.Create("Ubuntu", null, "img", 10, "s");
        t.SyncSucceededIn(Mock, t.SyncIn(Mock), Alias);
        var repo = new FakeVmTemplateRepository(t);

        await Observe(repo, t.Id, exists: false);

        Assert.Equal("NotFound/-", Shape(t, Mock));
        Assert.False(t.IsSyncedIn(Mock));
    }

    [Fact]
    public async Task AnObservationForAnUnknownCluster_Is404ClusterNotFound()
    {
        VmTemplate t = Waiting(out _);
        var repo = new FakeVmTemplateRepository(t);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Observe(repo, t.Id, exists: false, clusterId: "vn-hcm-1"));

        Assert.Equal("CLUSTER_NOT_FOUND", ex.Code);
        Assert.Empty(repo.Locked);
    }

    [Fact]
    public async Task AnObservationForNoTemplate_Is404TemplateNotFound()
    {
        var repo = new FakeVmTemplateRepository();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => Observe(repo, Guid.NewGuid(), exists: false));

        Assert.Equal("TEMPLATE_NOT_FOUND", ex.Code);
    }
}

using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the template reconciler: GET /internal/reconcile/templates returns what the worker can
// observe in one cluster (a provider id Titan holds) with the state Titan last recorded; every call is that cluster's
// heartbeat.
//
// Silent about: Postgres (InMemory runs the same LINQ, not the SQL EF generates from it).
public class TemplateReconcilerTests
{
    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    // A name shares one in-memory database between contexts, so a test can save and re-read through fresh ones.
    private static TitanDbContext NewDb(string? name = null)
    {
        string databaseName = $"template-reconcile-{Guid.NewGuid()}";
        if (name != null)
        {
            databaseName = $"template-reconcile-{name}";
        }
        return new(new DbContextOptionsBuilder<TitanDbContext>().UseInMemoryDatabase(databaseName).Options);
    }

    private static ListReconcilableTemplatesEndpoint TemplateEndpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string token, string? clusterId) =>
        Factory.Create<ListReconcilableTemplatesEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = token;
                if (clusterId != null)
                {
                    ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={clusterId}");
                }
            },
            new ListReconcilableTemplatesQueryHandler(new VmTemplateRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    private static VmTemplate SyncedIn(string name, string clusterId, string externalTemplateId)
    {
        var t = VmTemplate.Create(name, null, "img", 10, "m");
        t.SyncIn(clusterId);
        t.MarkCreatedIn(clusterId, externalTemplateId);
        return t;
    }

    [Fact] // one cluster's reconciler: the templates with a provider id IN THAT CLUSTER, with that cluster's existence and id
    public async Task TemplateEndpoint_ReturnsOnlyTemplatesWithAProviderIdInTheCluster_AndRecordsItsHeartbeat()
    {
        using var db = NewDb();
        var synced = SyncedIn("synced", "mock", "205");
        synced.SyncIn("singapore01");
        synced.MarkCreatedIn("singapore01", "3");
        var lost = SyncedIn("lost", "mock", "206");
        lost.MarkNotFoundIn("mock");
        var failedFirstSync = VmTemplate.Create("never", null, "img", 10, "m");
        failedFirstSync.SyncIn("mock");
        failedFirstSync.MarkRequestFailedIn("mock", "image not found", null);
        var otherClusterOnly = SyncedIn("singapore-only", "singapore01", "4");
        db.VmTemplates.AddRange(synced, lost, failedFirstSync, otherClusterOnly, VmTemplate.Create("pending", null, "img", 10, "m"));
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat(db);
        var ep = TemplateEndpoint(db, heartbeat, "secret", clusterId: "mock");

        await ep.HandleAsync(CancellationToken.None);

        var rows = ep.Response.OrderBy(r => r.ExternalTemplateId).Select(r => $"{r.ExternalTemplateId}:{r.CurrentExistence}").ToList();
        Assert.Equal(new[] { "205:Exists", "206:NotFound" }, rows);
        Assert.True((await heartbeat.SnapshotAsync(CancellationToken.None)).ContainsKey("templates/mock"));
    }

    [Fact] // a reconciler that does not say its cluster is asking for nothing anyone can answer
    public async Task TemplateEndpoint_WithoutACluster_Answers400_AndRecordsNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat(db);
        var ep = TemplateEndpoint(db, heartbeat, "secret", clusterId: null);

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(400, ep.HttpContext.Response.StatusCode);
        Assert.Empty((await heartbeat.SnapshotAsync(CancellationToken.None)));
    }

    [Fact] // the worker compares Exists and NotFound only: a re-sync that did not see the alias reads NotFound, one that did, Exists
    public async Task TemplateEndpoint_AReSyncReadsExistsOnlyWhenItSawTheAlias()
    {
        using var db = NewDb();
        var sawIt = SyncedIn("saw-it", "mock", "301");
        sawIt.MarkNotFoundIn("mock");
        sawIt.SyncIn("mock");
        sawIt.MarkObservedIn("mock");
        var didNotSeeIt = SyncedIn("did-not-see-it", "mock", "302");
        didNotSeeIt.MarkNotFoundIn("mock");
        didNotSeeIt.SyncIn("mock");
        db.VmTemplates.AddRange(sawIt, didNotSeeIt);
        await db.SaveChangesAsync();
        var ep = TemplateEndpoint(db, new ReconcilerHeartbeat(db), "secret", clusterId: "mock");

        await ep.HandleAsync(CancellationToken.None);

        var rows = ep.Response.OrderBy(r => r.ExternalTemplateId).Select(r => $"{r.ExternalTemplateId}:{r.CurrentExistence}").ToList();
        Assert.Equal(new[] { "301:Exists", "302:NotFound" }, rows);
    }

    [Fact] // the customer catalog: what the provider was last observed to hold — a template found gone is not offered
    public async Task TemplateCatalog_ListsOnlyWhatExists_NotALostOne()
    {
        using var db = NewDb();
        var synced = SyncedIn("synced", "mock", "205");
        var lost = SyncedIn("lost", "mock", "206");
        lost.MarkNotFoundIn("mock");
        db.VmTemplates.AddRange(synced, lost, SyncedIn("elsewhere", "singapore01", "3"), VmTemplate.Create("pending", null, "img", 10, "m"));
        await db.SaveChangesAsync();

        var names = (await new VmTemplateRepository(db).ListSyncedInClusterAsync("mock")).Select(t => t.Name).ToList();

        Assert.Equal(new[] { "synced" }, names);
    }

    // VmTemplateCluster.IsSynced written in SQL: Available, or Syncing with the alias seen. A re-sync that did not see it,
    // a lost alias and a give-up before any alias are not offered.
    [Fact]
    public async Task TemplateCatalog_OffersAnAvailableCluster_AndAReSyncThatSawTheAlias_AndNoOtherStatus()
    {
        using var db = NewDb();
        var available = SyncedIn("available", "mock", "1");
        var reSyncSawIt = SyncedIn("re-sync-saw-it", "mock", "2");
        reSyncSawIt.MarkNotFoundIn("mock");
        reSyncSawIt.SyncIn("mock");
        reSyncSawIt.MarkObservedIn("mock");
        var reSyncDidNot = SyncedIn("re-sync-did-not", "mock", "3");
        reSyncDidNot.MarkNotFoundIn("mock");
        reSyncDidNot.SyncIn("mock");
        var lost = SyncedIn("lost", "mock", "4");
        lost.MarkNotFoundIn("mock");
        var firstSync = VmTemplate.Create("first-sync", null, "img", 10, "m");
        firstSync.SyncIn("mock");
        var gaveUp = VmTemplate.Create("gave-up", null, "img", 10, "m");
        gaveUp.SyncIn("mock");
        gaveUp.MarkRequestFailedIn("mock", "x", null);
        db.VmTemplates.AddRange(available, reSyncSawIt, reSyncDidNot, lost, firstSync, gaveUp);
        await db.SaveChangesAsync();
        var repo = new VmTemplateRepository(db);

        var inCluster = (await repo.ListSyncedInClusterAsync("mock")).Select(t => t.Name).Order().ToList();
        var inAny = (await repo.ListSyncedInAnyClusterAsync()).Select(t => t.Name).Order().ToList();

        Assert.Equal(new[] { "available", "re-sync-saw-it" }, inCluster);
        Assert.Equal(inCluster, inAny);
    }

    [Fact] // a cluster asked into a template already saved is a NEW row, not an update of one that does not exist
    public async Task TemplateRepository_AClusterAddedToASavedTemplate_IsInserted()
    {
        using (var db = NewDb("cluster-added"))
        {
            db.VmTemplates.Add(SyncedIn("t", "mock", "205"));
            await db.SaveChangesAsync();
        }
        using (var db = NewDb("cluster-added"))
        {
            var repo = new VmTemplateRepository(db);
            var tracked = (await repo.FindByIdAsync((await db.VmTemplates.SingleAsync()).Id))!;
            tracked.SyncIn("singapore01");
            await repo.SaveAsync();
        }
        using (var db = NewDb("cluster-added"))
        {
            var reread = (await new VmTemplateRepository(db).ListAllAsync()).Single();
            Assert.Equal(new[] { "mock:Available", "singapore01:Syncing" },
                reread.Clusters.OrderBy(z => z.ClusterId).Select(z => $"{z.ClusterId}:{z.Status}"));
        }
    }
}

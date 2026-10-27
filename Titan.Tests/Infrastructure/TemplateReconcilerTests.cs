using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the template reconciler: GET /internal/reconcile/templates returns what the worker can
// observe in one zone (a provider id Titan holds) with the state Titan last recorded; every call is that zone's
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

    private static ListReconcilableTemplatesEndpoint TemplateEndpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string token, string? zoneId) =>
        Factory.Create<ListReconcilableTemplatesEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = token;
                if (zoneId != null)
                {
                    ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={zoneId}");
                }
            },
            new ListReconcilableTemplatesQueryHandler(new VmTemplateRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    private static VmTemplate SyncedIn(string name, string zoneId, string externalTemplateId)
    {
        var t = VmTemplate.Create(name, null, "img", 10, "m");
        t.RequestSyncIn(zoneId);
        t.MarkCreatedIn(zoneId, externalTemplateId);
        return t;
    }

    [Fact] // one zone's reconciler: the templates with a provider id IN THAT ZONE, with that zone's existence and id
    public async Task TemplateEndpoint_ReturnsOnlyTemplatesWithAProviderIdInTheZone_AndRecordsItsHeartbeat()
    {
        using var db = NewDb();
        var synced = SyncedIn("synced", "mock", "205");
        synced.RequestSyncIn("singapore01");
        synced.MarkCreatedIn("singapore01", "3");
        var lost = SyncedIn("lost", "mock", "206");
        lost.MarkNotFoundIn("mock");
        var failedFirstSync = VmTemplate.Create("never", null, "img", 10, "m");
        failedFirstSync.RequestSyncIn("mock");
        failedFirstSync.MarkRequestFailedIn("mock", "image not found", null);
        var otherZoneOnly = SyncedIn("singapore-only", "singapore01", "4");
        db.VmTemplates.AddRange(synced, lost, failedFirstSync, otherZoneOnly, VmTemplate.Create("pending", null, "img", 10, "m"));
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = TemplateEndpoint(db, heartbeat, "secret", zoneId: "mock");

        await ep.HandleAsync(CancellationToken.None);

        var rows = ep.Response.OrderBy(r => r.ExternalTemplateId).Select(r => $"{r.ExternalTemplateId}:{r.CurrentExistence}").ToList();
        Assert.Equal(new[] { "205:Exists", "206:NotFound" }, rows);
        Assert.NotNull(heartbeat.Snapshot()["templates/mock"].LastPollAt);
    }

    [Fact] // a reconciler that does not say its zone is asking for nothing anyone can answer
    public async Task TemplateEndpoint_WithoutAZone_Answers400_AndRecordsNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = TemplateEndpoint(db, heartbeat, "secret", zoneId: null);

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(400, ep.HttpContext.Response.StatusCode);
        Assert.Empty(heartbeat.Snapshot());
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

        var names = (await new VmTemplateRepository(db).ListSyncedInZoneAsync("mock")).Select(t => t.Name).ToList();

        Assert.Equal(new[] { "synced" }, names);
    }

    [Fact] // a zone asked into a template already saved is a NEW row, not an update of one that does not exist
    public async Task TemplateRepository_AZoneAddedToASavedTemplate_IsInserted()
    {
        using (var db = NewDb("zone-added"))
        {
            db.VmTemplates.Add(SyncedIn("t", "mock", "205"));
            await db.SaveChangesAsync();
        }
        using (var db = NewDb("zone-added"))
        {
            var repo = new VmTemplateRepository(db);
            var tracked = (await repo.FindByIdAsync((await db.VmTemplates.SingleAsync()).Id))!;
            tracked.RequestSyncIn("singapore01");
            await repo.SaveAsync();
        }
        using (var db = NewDb("zone-added"))
        {
            var reread = (await new VmTemplateRepository(db).ListAllAsync()).Single();
            Assert.Equal(new[] { "mock:Exists", "singapore01:NotCreated" },
                reread.Zones.OrderBy(z => z.ZoneId).Select(z => $"{z.ZoneId}:{z.CurrentExistence}"));
        }
    }
}

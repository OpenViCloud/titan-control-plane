using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with template syncs waiting under the old column meets the migration that gives each sync its kind and time
/// (titan-ops architecture/resource-lifecycle.md#shape): a waiting sync is a Sync, an open command of a cluster that waits
/// takes the request's key and id, other command rows keep theirs, and the view lists the waiting syncs with no tenant.
/// The old column name and keys below are the schema before it, by necessity. Category=Integration — its own CI step;
/// needs real Postgres.
[Trait("Category", "Integration")]
public sealed class TemplatePendingRequestMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261103135626_LoadBalancerPendingRequest";

    private readonly TestPostgres _container = new();

    public Task InitializeAsync() => _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private async Task MigrateToAsync(string migration)
    {
        await using TitanDbContext db = NewDbContext();
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private async Task MigrateToLatestAsync()
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    // The migrations seed the catalog: the tests sync its templates rather than add their own.
    private async Task<List<Guid>> SeededTemplatesAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM core.vm_templates ORDER BY name").ToListAsync();
    }

    // A cluster row as the image before this migration wrote it: the sync's id in pending_intent_id.
    private async Task<Guid?> SeedClusterRowAsync(Guid templateId, string clusterId, bool waiting)
    {
        Guid? intent = waiting ? Guid.NewGuid() : null;
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_template_clusters (template_id, cluster_id, current_existence, desired_existence, pending_intent_id, updated_at)
VALUES ({templateId}, {clusterId}, 'NotCreated', 'Exists', {intent}, now())");
        return intent;
    }

    private async Task AddCommandAsync(string key, string clusterId, string payload, bool done = false)
    {
        await using TitanDbContext seed = NewDbContext();
        await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, key, ProviderCommandKinds.TemplateSync, clusterId, payload,
            done ? "Done" : "Pending");
    }

    private async Task<VmTemplateCluster> RowAsync(Guid templateId, string clusterId)
    {
        await using TitanDbContext db = NewDbContext();
        VmTemplate template = await db.VmTemplates.Include(t => t.Clusters).SingleAsync(t => t.Id == templateId);
        return template.Clusters.Single(c => c.ClusterId == clusterId);
    }

    [Fact]
    public async Task EveryWaitingSync_GetsTheKindSyncAndATime_ARestingRowNone()
    {
        await MigrateToAsync(MigrationBefore);
        List<Guid> templates = await SeededTemplatesAsync();
        Guid? waiting = await SeedClusterRowAsync(templates[0], "cluster-a", waiting: true);
        await SeedClusterRowAsync(templates[0], "cluster-b", waiting: false);

        await MigrateToLatestAsync();

        VmTemplateCluster sync = await RowAsync(templates[0], "cluster-a");
        Assert.Equal((VmTemplateOperationKind.Sync, waiting!.Value), (sync.Pending!.Kind, sync.Pending.Id));
        Assert.True(sync.Pending.AskedAt > DateTimeOffset.UnixEpoch);
        Assert.Null((await RowAsync(templates[0], "cluster-b")).Pending);
    }

    [Fact]
    public async Task AnOpenCommandOfAClusterThatWaits_TakesTheRequestsKeyAndId_TheOthersAreLeftAlone()
    {
        await MigrateToAsync(MigrationBefore);
        List<Guid> templates = await SeededTemplatesAsync();
        Guid template = templates[0];
        Guid? waitingInA = await SeedClusterRowAsync(template, "cluster-a", waiting: true);
        string openKey = $"template-sync-{template}-cluster-a";
        await AddCommandAsync(openKey, "cluster-a", $$"""{"templateId":"{{template}}","clusterId":"cluster-a","name":"x"}""");
        // A cluster with nothing waiting: its open row is a repeat the old sync-all sent; no request is there to name.
        await SeedClusterRowAsync(template, "cluster-b", waiting: false);
        string restingKey = $"template-sync-{template}-cluster-b";
        await AddCommandAsync(restingKey, "cluster-b", "{}");
        // A finished row under the key of a cluster that waits (its report was lost) is history: left keyed as it was.
        Guid other = templates[1];
        await SeedClusterRowAsync(other, "cluster-a", waiting: true);
        string finishedKey = $"template-sync-{other}-cluster-a";
        await AddCommandAsync(finishedKey, "cluster-a", "{}", done: true);

        await MigrateToLatestAsync();

        await using TitanDbContext read = NewDbContext();
        Dictionary<string, string> payloadByKey = await read.ProviderCommands.ToDictionaryAsync(c => c.Key, c => c.Payload);
        Assert.Equal(3, payloadByKey.Count);
        string movedPayload = payloadByKey[$"{openKey}-{waitingInA}"];
        using (JsonDocument moved = JsonDocument.Parse(movedPayload))
        {
            Assert.Equal(waitingInA.ToString(), moved.RootElement.GetProperty("requestId").GetString());
            // The rest of the payload is kept.
            Assert.Equal("x", moved.RootElement.GetProperty("name").GetString());
        }
        Assert.Equal("{}", payloadByKey[restingKey]);
        Assert.Equal("{}", payloadByKey[finishedKey]);
    }

    [Fact]
    public async Task TheView_ListsAWaitingSync_WithNoTenant_AndItsCluster_AndARowOfTheNewModelInserts()
    {
        await MigrateToLatestAsync();
        Guid templateId = (await SeededTemplatesAsync())[0];
        Guid requestId;
        await using (TitanDbContext db = NewDbContext())
        {
            VmTemplate template = await db.VmTemplates.Include(t => t.Clusters).SingleAsync(t => t.Id == templateId);
            requestId = template.SyncIn("cluster-a").Id;
            await db.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        List<string> rows = await read.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind || '|' || request_id::text || '|' || cluster_id || '|' || coalesce(tenant_id::text, 'none') AS \"Value\" FROM core.pending_requests WHERE id = {templateId}")
            .ToListAsync();
        Assert.Equal([$"template|Sync|{requestId}|cluster-a|none"], rows);
    }

    // Down puts the view of the earlier phases back, without the cluster column, before the kind column goes, and the old
    // column name.
    [Fact]
    public async Task Down_RestoresTheColumnAndTheEarlierView()
    {
        await MigrateToLatestAsync();
        await MigrateToAsync(MigrationBefore);

        Guid templateId = (await SeededTemplatesAsync())[0];
        await SeedClusterRowAsync(templateId, "cluster-a", waiting: true);
        await using TitanDbContext read = NewDbContext();
        int listed = await read.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM core.pending_requests WHERE id = {templateId}")
            .SingleAsync();
        Assert.Equal(0, listed);
        int clusterColumns = await read.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'pending_requests' AND column_name = 'cluster_id'")
            .SingleAsync();
        Assert.Equal(0, clusterColumns);
    }
}

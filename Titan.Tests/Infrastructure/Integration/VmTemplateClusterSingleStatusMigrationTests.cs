using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with template clusters under the existence pair and the pending kind meets the migration that gives a cluster
/// one status (titan-ops features/catalog/design.md#template-state-is-existence): each combination of the old columns becomes
/// the status the migration's CASE names, in its order, and observed_state keeps Available only while a sync waits on a
/// cluster that was seen holding the alias. The view of waiting requests reads a cluster's kind off its status, and Down
/// puts the old columns back. The old column names below are the schema before it, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VmTemplateClusterSingleStatusMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261108054110_VolumeSingleStatus";
    private const string ThisMigration = "20261108105433_VmTemplateClusterSingleStatus";

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

    // The model of today writes the schema of today, not the one this migration left.
    private async Task MigrateToLatestAsync()
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    // The migrations seed the catalog: the tests put their cluster rows under its first template.
    private async Task<Guid> SeededTemplateAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return (await db.Database.SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM core.vm_templates ORDER BY name").ToListAsync())[0];
    }

    // A cluster row as the image before this migration wrote it. A waiting sync has its id and the kind Sync; a cluster that
    // never held an alias has no provider id; a give-up leaves its reason.
    private async Task<string> SeedClusterAsync(Guid templateId, string currentExistence, bool waiting = false,
        bool aliasRecorded = true, string? reason = null, bool kindWithoutAnId = false)
    {
        string clusterId = "c-" + Guid.NewGuid().ToString("N")[..10];
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        string? kind = null;
        if (waiting)
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
            kind = "Sync";
        }
        if (kindWithoutAnId)
        {
            kind = "Sync";
        }
        string? alias = null;
        if (aliasRecorded)
        {
            alias = "tpl-" + clusterId;
        }
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_template_clusters (template_id, cluster_id, current_existence, desired_existence, external_template_id,
                                       pending_request_id, pending_request_kind, pending_request_asked_at, request_failure_reason, updated_at)
VALUES ({templateId}, {clusterId}, {currentExistence}, 'Exists', {alias}, {requestId}, {kind}, {askedAt}, {reason}, now())");
        return clusterId;
    }

    // Each cluster's status and observed_state after the migration, read in SQL: "-" stands for null.
    private async Task<Dictionary<string, string>> StatusAndObservedStateAsync()
    {
        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT cluster_id || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.vm_template_clusters")
            .ToListAsync();
        Dictionary<string, string> byCluster = new();
        foreach (string row in rows)
        {
            int cut = row.IndexOf('|');
            byCluster[row[..cut]] = row[(cut + 1)..];
        }
        return byCluster;
    }

    [Fact]
    public async Task EveryOldCombination_BecomesTheStatusTheCaseNames_AndObservedStateIsAvailableOnlyWhileASyncWaitsOnAClusterThatHeldTheAlias()
    {
        await MigrateToAsync(MigrationBefore);
        Guid template = await SeededTemplateAsync();
        // One row per branch of the CASE, in its order.
        string firstSync = await SeedClusterAsync(template, "NotCreated", waiting: true, aliasRecorded: false);
        string reSyncSeen = await SeedClusterAsync(template, "Exists", waiting: true);
        string reSyncLost = await SeedClusterAsync(template, "NotFound", waiting: true);
        // Reached only by the reconciler's observation of a cluster never asked, then a sync asked.
        string observedGoneThenSync = await SeedClusterAsync(template, "NotFound", waiting: true, aliasRecorded: false);
        string gaveUp = await SeedClusterAsync(template, "NotCreated", aliasRecorded: false, reason: "provider unreachable");
        string neverReachedWithoutAReason = await SeedClusterAsync(template, "NotCreated", aliasRecorded: false);
        // Reached only through a callback (the reconciler lists rows with an alias): NotFound, no alias, no give-up.
        string observedGone = await SeedClusterAsync(template, "NotFound", aliasRecorded: false);
        string observedGoneAfterAGiveUp = await SeedClusterAsync(template, "NotFound", aliasRecorded: false, reason: "provider unreachable");
        string available = await SeedClusterAsync(template, "Exists");
        string availableWithAReason = await SeedClusterAsync(template, "Exists", reason: "provider unreachable");
        string lost = await SeedClusterAsync(template, "NotFound");
        string lostWithAReason = await SeedClusterAsync(template, "NotFound", reason: "provider unreachable");
        // Combinations the CASE does not name end in its ELSE: Available.
        string notCreatedWithAnAlias = await SeedClusterAsync(template, "NotCreated");
        string aKindWithoutAnId = await SeedClusterAsync(template, "Exists", kindWithoutAnId: true);

        await MigrateToAsync(ThisMigration);

        Dictionary<string, string> byCluster = await StatusAndObservedStateAsync();
        Assert.Equal("Syncing|-", byCluster[firstSync]);
        Assert.Equal("Syncing|Available", byCluster[reSyncSeen]);
        Assert.Equal("Syncing|-", byCluster[reSyncLost]);
        Assert.Equal("Syncing|-", byCluster[observedGoneThenSync]);
        Assert.Equal("Failed|-", byCluster[gaveUp]);
        Assert.Equal("Failed|-", byCluster[neverReachedWithoutAReason]);
        Assert.Equal("NotFound|-", byCluster[observedGone]);
        Assert.Equal("Failed|-", byCluster[observedGoneAfterAGiveUp]);
        Assert.Equal("Available|-", byCluster[available]);
        Assert.Equal("Available|-", byCluster[availableWithAReason]);
        Assert.Equal("NotFound|-", byCluster[lost]);
        Assert.Equal("NotFound|-", byCluster[lostWithAReason]);
        Assert.Equal("Available|-", byCluster[notCreatedWithAnAlias]);
        Assert.Equal("Available|-", byCluster[aKindWithoutAnId]);
    }

    // What the domain reads off the migrated row: the status, the reason kept, and whether the cluster holds the alias.
    [Fact]
    public async Task AMigratedCluster_ReadsBackItsStatus_ItsReason_AndWhetherTheClusterHoldsTheAlias()
    {
        await MigrateToAsync(MigrationBefore);
        Guid templateId = await SeededTemplateAsync();
        string reSyncSeen = await SeedClusterAsync(templateId, "Exists", waiting: true, reason: "provider unreachable");
        string reSyncLost = await SeedClusterAsync(templateId, "NotFound", waiting: true);
        string lostWithAReason = await SeedClusterAsync(templateId, "NotFound", reason: "provider unreachable");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        VmTemplate template = await db.VmTemplates.Include(t => t.Clusters).SingleAsync(t => t.Id == templateId);
        VmTemplateCluster seen = template.Clusters.Single(c => c.ClusterId == reSyncSeen);
        Assert.Equal(VmTemplateClusterStatus.Syncing, seen.Status);
        Assert.Equal(VmTemplateClusterStatus.Available, seen.InstanceStatus);
        Assert.True(seen.IsSynced);
        Assert.Equal("provider unreachable", seen.Operation.FailureReason);
        Assert.NotNull(seen.Operation.PendingId);
        VmTemplateCluster notSeen = template.Clusters.Single(c => c.ClusterId == reSyncLost);
        Assert.Equal(VmTemplateClusterStatus.Syncing, notSeen.Status);
        Assert.Null(notSeen.InstanceStatus);
        Assert.False(notSeen.IsSynced);
        VmTemplateCluster gone = template.Clusters.Single(c => c.ClusterId == lostWithAReason);
        Assert.Equal(VmTemplateClusterStatus.NotFound, gone.Status);
        Assert.Null(gone.Operation.PendingId);
        Assert.Equal("provider unreachable", gone.Operation.FailureReason);
    }

    [Fact]
    public async Task TheOldColumnsAreGone_AndTheStatusAndObservedStateAreThere()
    {
        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> columns = await db.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'vm_template_clusters'")
            .ToListAsync();
        Assert.DoesNotContain("current_existence", columns);
        Assert.DoesNotContain("desired_existence", columns);
        Assert.DoesNotContain("pending_request_kind", columns);
        Assert.Contains("status", columns);
        Assert.Contains("observed_state", columns);
    }

    // The view of waiting requests names a cluster's kind from its status, the word PendingRequestReader lowercases into
    // the command key: a wrong word here lists nothing as stuck and reads green.
    [Fact]
    public async Task TheViewOfWaitingRequests_NamesASyncingClustersKind_FromItsStatus()
    {
        await MigrateToAsync(MigrationBefore);
        Guid templateId = await SeededTemplateAsync();
        string firstSync = await SeedClusterAsync(templateId, "NotCreated", waiting: true, aliasRecorded: false);
        string reSync = await SeedClusterAsync(templateId, "Exists", waiting: true);
        string resting = await SeedClusterAsync(templateId, "Exists");
        string gaveUp = await SeedClusterAsync(templateId, "NotCreated", aliasRecorded: false, reason: "x");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQuery<string>($"SELECT cluster_id || '|' || kind || '|' || coalesce(tenant_id::text, 'none') AS \"Value\" FROM core.pending_requests WHERE resource = 'template' AND id = {templateId}")
            .ToListAsync();
        Assert.Equal(
            new[] { $"{firstSync}|Sync|none", $"{reSync}|Sync|none" }.Order(StringComparer.Ordinal),
            rows.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(rows, r => r.StartsWith(resting, StringComparison.Ordinal) || r.StartsWith(gaveUp, StringComparison.Ordinal));
    }

    // A cluster written by the model of today stores its status and what it was seen holding by name.
    [Fact]
    public async Task AClusterWrittenNow_StoresItsStatusAndObservedStateByName()
    {
        await MigrateToLatestAsync();
        Guid templateId = await SeededTemplateAsync();
        Guid request;
        await using (TitanDbContext db = NewDbContext())
        {
            VmTemplate template = await db.VmTemplates.Include(t => t.Clusters).SingleAsync(t => t.Id == templateId);
            template.SyncSucceededIn("cluster-a", template.SyncIn("cluster-a"), "tpl-a");
            template.MarkNotFoundIn("cluster-a");
            request = template.SyncIn("cluster-a");
            template.MarkObservedIn("cluster-a");
            template.SyncIn("cluster-b");
            await db.SaveChangesAsync();
        }

        Dictionary<string, string> byCluster = await StatusAndObservedStateAsync();
        Assert.Equal("Syncing|Available", byCluster["cluster-a"]);
        Assert.Equal("Syncing|-", byCluster["cluster-b"]);
        await using TitanDbContext read = NewDbContext();
        List<string> waiting = await read.Database
            .SqlQuery<string>($"SELECT kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE resource = 'template' AND cluster_id = 'cluster-a' AND id = {templateId}")
            .ToListAsync();
        Assert.Equal([$"Sync|{request}"], waiting);
    }

    [Fact]
    public async Task Down_PutsTheOldColumnsBackFromTheStatus_AndTheViewReadsTheKindColumnAgain()
    {
        await MigrateToAsync(MigrationBefore);
        Guid templateId = await SeededTemplateAsync();
        string firstSync = await SeedClusterAsync(templateId, "NotCreated", waiting: true, aliasRecorded: false);
        string reSyncSeen = await SeedClusterAsync(templateId, "Exists", waiting: true);
        string reSyncLost = await SeedClusterAsync(templateId, "NotFound", waiting: true);
        string gaveUp = await SeedClusterAsync(templateId, "NotCreated", aliasRecorded: false, reason: "x");
        string observedGone = await SeedClusterAsync(templateId, "NotFound", aliasRecorded: false);
        string available = await SeedClusterAsync(templateId, "Exists");
        string lost = await SeedClusterAsync(templateId, "NotFound");
        await MigrateToAsync(ThisMigration);

        await MigrateToAsync(MigrationBefore);

        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database.SqlQueryRaw<string>(
            "SELECT cluster_id || '|' || current_existence || '|' || desired_existence || '|' || coalesce(pending_request_kind, '-') AS \"Value\" FROM core.vm_template_clusters")
            .ToListAsync();
        Assert.Contains($"{firstSync}|NotCreated|Exists|Sync", rows);
        Assert.Contains($"{reSyncSeen}|Exists|Exists|Sync", rows);
        Assert.Contains($"{reSyncLost}|NotFound|Exists|Sync", rows);
        Assert.Contains($"{gaveUp}|NotCreated|Exists|-", rows);
        Assert.Contains($"{observedGone}|NotFound|Exists|-", rows);
        Assert.Contains($"{available}|Exists|Exists|-", rows);
        Assert.Contains($"{lost}|NotFound|Exists|-", rows);
        List<string> waiting = await db.Database
            .SqlQuery<string>($"SELECT cluster_id || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'template' AND id = {templateId}")
            .ToListAsync();
        Assert.Contains($"{firstSync}|Sync", waiting);
        Assert.Contains($"{reSyncSeen}|Sync", waiting);
        Assert.Contains($"{reSyncLost}|Sync", waiting);
        Assert.Equal(3, waiting.Count);
    }
}

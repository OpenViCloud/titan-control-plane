using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// Rows written before the tenant column meet the migration that gives each a tenant lane
/// (titan-ops features/provider/design.md#tenant-lane): a row whose payload names a tenant's Incus project gets that
/// tenant, template.sync none; the lane column follows the kind; Down drops both columns and both indexes.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class ProviderCommandTenantLaneMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261110160823_VmMetricSamples";

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

    private async Task<int> CountAsync(string sql)
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }

    private Task<int> LeasedRowIndexesAsync() => CountAsync(
        "SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'core' AND tablename = 'provider_commands' " +
        $"AND indexname IN ('{ProviderCommandConfiguration.LeasedPerTenantIndex}', " +
        $"'{ProviderCommandConfiguration.LeasedNoTenantIndex}')");

    private Task<int> TenantColumnsAsync() => CountAsync(
        "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' " +
        "AND table_name = 'provider_commands' AND column_name IN ('tenant_id', 'lane')");

    // The schema of the migration before, by necessity: no tenant_id, no lane.
    private static Task InsertBeforeAsync(TitanDbContext db, string key, string kind, string payload, string status) =>
        db.Database.ExecuteSqlRawAsync(
            "INSERT INTO core.provider_commands (id, key, kind, cluster_id, payload, status, attempts, available_at, " +
            "created_at, version) VALUES ({0}, {1}, {2}, 'cluster-a', {3}::jsonb, {4}, 0, now(), now(), 0)",
            Guid.NewGuid(), key, kind, payload, status);

    [Fact]
    public async Task ARowNamingATenantsProject_GetsThatTenant_TemplateSyncNone_AndTheLaneFollowsTheKind()
    {
        await MigrateToAsync(MigrationBefore);
        Guid tenant = Guid.NewGuid();
        string project = "t-" + tenant.ToString("N");
        string vmDelete = $"vm-delete-{Guid.NewGuid()}-{Guid.NewGuid()}";
        string backup = $"vm-backup-{Guid.NewGuid()}-20261111";
        string sync = $"template-sync-{Guid.NewGuid()}-cluster-a-{Guid.NewGuid()}";
        await using (TitanDbContext seed = NewDbContext())
        {
            await InsertBeforeAsync(seed, vmDelete, ProviderCommandKinds.VmDelete,
                $"{{\"vmId\":\"x\",\"projectName\":\"{project}\"}}", "Leased");
            await InsertBeforeAsync(seed, backup, ProviderCommandKinds.VmBackup,
                $"{{\"vmId\":\"x\",\"projectName\":\"{project}\"}}", "Leased");
            await InsertBeforeAsync(seed, sync, ProviderCommandKinds.TemplateSync, "{\"templateId\":\"x\"}", "Pending");
        }

        await MigrateToLatestAsync();

        Assert.Equal(2, await LeasedRowIndexesAsync());
        await using TitanDbContext read = NewDbContext();
        Dictionary<string, Guid?> tenants = await read.ProviderCommands.AsNoTracking()
            .ToDictionaryAsync(c => c.Key, c => c.TenantId);
        Assert.Equal(tenant, tenants[vmDelete]);
        Assert.Equal(tenant, tenants[backup]);
        Assert.Null(tenants[sync]);
        List<string> lanes = await read.Database
            .SqlQueryRaw<string>("SELECT kind || '|' || lane AS \"Value\" FROM core.provider_commands ORDER BY kind")
            .ToListAsync();
        Assert.Equal(["template.sync|command", "vm.backup|backup", "vm.delete|command"], lanes);
    }

    [Fact]
    public async Task Down_DropsTheIndexAndBothColumns_AndKeepsTheRows()
    {
        await MigrateToLatestAsync();
        await using (TitanDbContext seed = NewDbContext())
        {
            seed.ProviderCommands.Add(ProviderCommand.Create($"vm-delete-{Guid.NewGuid()}-{Guid.NewGuid()}",
                ProviderCommandKinds.VmDelete, "cluster-a", "{}", DateTimeOffset.UtcNow, Guid.NewGuid()));
            await seed.SaveChangesAsync();
        }

        await MigrateToAsync(MigrationBefore);

        Assert.Equal(0, await LeasedRowIndexesAsync());
        Assert.Equal(0, await TenantColumnsAsync());
        Assert.Equal(1, await CountAsync("SELECT count(*)::int AS \"Value\" FROM core.provider_commands"));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// <summary>
/// The rename to cluster keeps every row: the ids (singapore01, local-mac) do not change, only the names of the
/// columns and of the template table that hold them. EF scaffolds a renamed table as drop + create, which would empty
/// the per-cluster sync state of every template; this measures that the migration renames instead.
/// The old names below are the schema before the migration, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClusterRenameMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261027032443_DropClusterTables";

    private readonly TestPostgres _container = new();

    public Task InitializeAsync() => _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    [Fact]
    public async Task ATemplatesSyncState_WrittenBeforeTheRename_IsReadUnderItsClusterAfterIt()
    {
        Guid templateId;
        await using (var db = NewDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
            templateId = await db.Database
                .SqlQueryRaw<Guid>("SELECT id AS \"Value\" FROM core.vm_templates WHERE name = 'Ubuntu-22.04-Standard'")
                .SingleAsync();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.vm_template_zones (template_id, zone_id, current_existence, desired_existence, external_template_id, updated_at) " +
                "VALUES ({0}, 'local-mac', 'Exists', 'Exists', {1}, now())",
                templateId, $"tpl-{templateId}");
        }

        await using (var db = NewDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = NewDbContext())
        {
            var template = await db.VmTemplates.Include(t => t.Clusters).SingleAsync(t => t.Id == templateId);
            Assert.Equal($"tpl-{templateId}", template.ProviderIdIn("local-mac"));
        }
    }

    [Fact]
    public async Task NoTable_KeepsAZoneColumn()
    {
        await using var db = NewDbContext();
        await db.Database.MigrateAsync();

        var left = await db.Database
            .SqlQueryRaw<string>(
                "SELECT table_name || '.' || column_name AS \"Value\" FROM information_schema.columns " +
                "WHERE table_schema = 'core' AND (column_name LIKE '%zone%' OR table_name LIKE '%zone%')")
            .ToListAsync();

        Assert.Empty(left);
    }
}

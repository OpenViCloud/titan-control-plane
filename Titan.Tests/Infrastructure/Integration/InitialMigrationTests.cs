using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// <summary>
/// The migration history starts at Initial (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#migrations): an empty database
/// is built by Initial and the migrations after it. What carries risk is a migration ordered before Initial
/// (a database built from it would miss the base schema), and the catalog seeds the first customer needs
/// (templates and offerings) not being part of the result.
/// Category=Integration — its own CI step; needs real Postgres.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InitialMigrationTests : IAsyncLifetime
{
    private readonly TestPostgres _container = new();

    public Task InitializeAsync() => _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    // Whole statements, not a table name spliced in: a raw query takes no identifier as a parameter.
    private static Task<int> CountAsync(TitanDbContext db, string countStatement) =>
        db.Database.SqlQueryRaw<int>(countStatement).SingleAsync();

    [Fact]
    public async Task AnEmptyDatabase_IsBuiltFromInitial_WithTheCatalogSeeded()
    {
        await using (var db = NewDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = NewDbContext())
        {
            var history = await db.Database
                .SqlQueryRaw<string>("SELECT \"MigrationId\" AS \"Value\" FROM public.\"__EFMigrationsHistory\" ORDER BY \"MigrationId\"")
                .ToListAsync();
            Assert.NotEmpty(history);
            Assert.EndsWith("_Initial", history[0]);
            Assert.Single(history, id => id.EndsWith("_Initial"));

            var templates = await db.Database
                .SqlQueryRaw<string>("SELECT name AS \"Value\" FROM core.vm_templates ORDER BY name")
                .ToListAsync();
            Assert.Equal(["Alma-9-DB", "Rocky-9-App", "Ubuntu-22.04-Standard"], templates);
            Assert.Equal(4, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.vm_offerings"));
            Assert.Equal(1, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.volume_offerings"));
            Assert.Equal(1, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.snapshot_offerings"));
            Assert.Equal(0, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.vm_template_clusters"));
        }
    }
}

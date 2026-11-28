using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// <summary>
/// The migration history starts at Initial (titan-ops architecture/code-structure.md#migrations): an empty database
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

            // The runtime catalog of OVC Function (titan-ops features/ovc-function/design.md#runtimes): three rows, pinned
            // by digest, and no function cluster row until a sync-all.
            var runtimes = await db.Database
                .SqlQueryRaw<string>("SELECT name || '|' || image || '|' || architecture || '|' || is_listed::text || '|' || coalesce(support_ends_on::text, 'none') AS \"Value\" FROM core.function_runtimes ORDER BY name")
                .ToListAsync();
            Assert.Equal(
                [
                    "go|ghcr.io/openvicloud/titan-function-runtimes/go@sha256:f253fc8f929cefbfdf899cdf8804374c0eb78e23d650f552840ea9e334d116dd|amd64|true|none",
                    "nodejs26|ghcr.io/openvicloud/titan-function-runtimes/nodejs26@sha256:9f06d060020e71de5e31fa6a8bfcd6b85cca5491282df2b28abeec4b1e386c3c|amd64|true|2029-04-30",
                    "python3.15|ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:5479dbe0d05ec82ba2ba0e6995a95619b231019639a7bf9aeb393a19d61c5a0b|amd64|true|2031-10-31",
                ],
                runtimes);
            Assert.Equal(0, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.function_runtime_clusters"));
            // No function and no version until a tenant makes one (titan-ops features/ovc-function/design.md#function-lifecycle).
            Assert.Equal(0, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.functions"));
            Assert.Equal(0, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.function_versions"));
            // No secret until a tenant gives one (titan-ops features/ovc-function/design.md#function-secrets).
            Assert.Equal(0, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.function_secrets"));
            Assert.Equal(0, await CountAsync(db, "SELECT count(*)::int AS \"Value\" FROM core.function_secret_versions"));
        }
    }
}

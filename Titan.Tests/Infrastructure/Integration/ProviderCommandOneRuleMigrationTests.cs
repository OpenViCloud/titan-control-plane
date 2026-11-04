using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database whose provider command rows carry a rerun mode meets the migration that gives every key one rule
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request): the column goes, every row keeps its status and
/// payload, and the index the retention pass reads is there. Down gives each row the mode the image before this
/// migration writes for its key. The column name and its values below are the schema before it, by necessity. Category=Integration — its own CI step; needs real
/// Postgres.
[Trait("Category", "Integration")]
public sealed class ProviderCommandOneRuleMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261103155659_TemplatePendingRequest";
    private const string RetentionIndex = "IX_provider_commands_status_created_at";

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

    private Task<int> RerunColumnsAsync() => CountAsync(
        "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'provider_commands' AND column_name = 'rerun'");

    private Task<int> RetentionIndexesAsync() => CountAsync(
        $"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'core' AND tablename = 'provider_commands' AND indexname = '{RetentionIndex}'");

    [Fact]
    public async Task TheRerunColumnGoes_EveryRowKeepsItsStatusAndPayload_AndTheRetentionIndexIsThere()
    {
        await MigrateToAsync(MigrationBefore);
        string vmDelete = $"vm-delete-{Guid.NewGuid()}-{Guid.NewGuid()}";
        string backup = $"vm-backup-{Guid.NewGuid()}-20261103";
        string failedBackup = $"volume-backup-{Guid.NewGuid()}-20261103";
        await using (TitanDbContext seed = NewDbContext())
        {
            await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, vmDelete, ProviderCommandKinds.VmDelete, "cluster-a",
                "{\"n\":1}", "Pending", "AfterAnyFinish");
            await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, backup, ProviderCommandKinds.VmBackup, "cluster-a",
                "{\"n\":2}", "Done", "AfterFailureOnly");
            await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, failedBackup, ProviderCommandKinds.VolumeBackup,
                "cluster-a", "{\"n\":3}", "Failed", "AfterFailureOnly");
        }

        await MigrateToLatestAsync();

        Assert.Equal(0, await RerunColumnsAsync());
        Assert.Equal(1, await RetentionIndexesAsync());
        await using TitanDbContext read = NewDbContext();
        Dictionary<string, (ProviderCommandStatus, string)> rows = await read.ProviderCommands
            .ToDictionaryAsync(c => c.Key, c => (c.Status, c.Payload));
        Assert.Equal(3, rows.Count);
        Assert.Equal((ProviderCommandStatus.Pending, "{\"n\": 1}"), rows[vmDelete]);
        Assert.Equal((ProviderCommandStatus.Done, "{\"n\": 2}"), rows[backup]);
        Assert.Equal((ProviderCommandStatus.Failed, "{\"n\": 3}"), rows[failedBackup]);
    }

    // A row written by the new model has no mode; Down derives it from the key: the two backups AfterFailureOnly, every
    // other key AfterAnyFinish, and the column has no default again, as before.
    [Fact]
    public async Task Down_RestoresTheColumn_WithTheModeTheImageBeforeWritesForItsKey_AndDropsTheIndex()
    {
        await MigrateToLatestAsync();
        string vmBackup = $"vm-backup-{Guid.NewGuid()}-20261103";
        string volumeBackup = $"volume-backup-{Guid.NewGuid()}-20261103";
        string vmDelete = $"vm-delete-{Guid.NewGuid()}-{Guid.NewGuid()}";
        string templateSync = $"template-sync-{Guid.NewGuid()}-cluster-a-{Guid.NewGuid()}";
        await using (TitanDbContext seed = NewDbContext())
        {
            seed.ProviderCommands.AddRange(
                ProviderCommand.Create(vmBackup, ProviderCommandKinds.VmBackup, "cluster-a", "{}", DateTimeOffset.UtcNow),
                ProviderCommand.Create(volumeBackup, ProviderCommandKinds.VolumeBackup, "cluster-a", "{}", DateTimeOffset.UtcNow),
                ProviderCommand.Create(vmDelete, ProviderCommandKinds.VmDelete, "cluster-a", "{}", DateTimeOffset.UtcNow),
                ProviderCommand.Create(templateSync, ProviderCommandKinds.TemplateSync, "cluster-a", "{}", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        await MigrateToAsync(MigrationBefore);

        Assert.Equal(1, await RerunColumnsAsync());
        Assert.Equal(0, await RetentionIndexesAsync());
        await using TitanDbContext read = NewDbContext();
        List<string> modes = await read.Database
            .SqlQueryRaw<string>("SELECT key || '|' || rerun AS \"Value\" FROM core.provider_commands ORDER BY key")
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                $"{templateSync}|AfterAnyFinish", $"{vmBackup}|AfterFailureOnly", $"{vmDelete}|AfterAnyFinish",
                $"{volumeBackup}|AfterFailureOnly",
            }.Order(StringComparer.Ordinal),
            modes.Order(StringComparer.Ordinal));
        int defaults = await CountAsync(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'provider_commands' AND column_name = 'rerun' AND column_default IS NOT NULL");
        Assert.Equal(0, defaults);
    }
}

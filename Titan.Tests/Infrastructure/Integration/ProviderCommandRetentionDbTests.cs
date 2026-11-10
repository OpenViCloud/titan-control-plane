using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The retention pass on Postgres (titan-ops architecture/resource-lifecycle.md#one-key-per-request): the rule runs as
/// one DELETE, which InMemory cannot run. Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ProviderCommandRetentionDbTests(PostgresFixture pg)
{
    private static ProviderCommand Row(string key, ProviderCommandStatus status, DateTimeOffset createdAt)
    {
        ProviderCommand row = ProviderCommand.Create(key, ProviderCommandKinds.VmDelete, "mock", "{}", createdAt);
        Guid lease = Guid.NewGuid();
        if (status != ProviderCommandStatus.Pending)
        {
            row.Lease(lease, createdAt);
        }
        if (status == ProviderCommandStatus.Done)
        {
            row.Complete(lease, createdAt);
        }
        if (status == ProviderCommandStatus.Failed)
        {
            row.Fail(lease, "gave up", retryable: false, createdAt);
        }
        return row;
    }

    [Fact]
    public async Task OnePass_DeletesExactlyTheFinishedRowsOlderThanThirtyDays_AndTodaysFailedBackupStaysToReopen()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset old = now.AddDays(-31);
        DateTimeOffset young = now.AddDays(-29);
        string run = Guid.NewGuid().ToString("N");
        global::Titan.Domain.Aggregates.Vm.Vm backupVm =
            global::Titan.Domain.Aggregates.Vm.Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "vm-x", "m", "tpl", "mock", 20);
        Guid vmId = backupVm.Id;
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        string backupKey = $"vm-backup-{vmId}-{today.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        var rows = new Dictionary<string, ProviderCommand>
        {
            ["old-done"] = Row($"test-{run}-old-done", ProviderCommandStatus.Done, old),
            ["old-failed"] = Row($"test-{run}-old-failed", ProviderCommandStatus.Failed, old),
            ["old-pending"] = Row($"test-{run}-old-pending", ProviderCommandStatus.Pending, old),
            ["old-leased"] = Row($"test-{run}-old-leased", ProviderCommandStatus.Leased, old),
            ["young-done"] = Row($"test-{run}-young-done", ProviderCommandStatus.Done, young),
            ["young-failed"] = Row($"test-{run}-young-failed", ProviderCommandStatus.Failed, young),
            ["todays-failed-backup"] = Row(backupKey, ProviderCommandStatus.Failed, now),
        };
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.ProviderCommands.AddRange(rows.Values);
            await seed.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            int removed = await ProviderCommandRetention.RunOnceAsync(new ProviderCommandRepository(db), 30, now,
                NullLogger.Instance, default);
            Assert.True(removed >= 2, $"removed {removed}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        List<Guid> ids = rows.Values.Select(r => r.Id).ToList();
        HashSet<Guid> left = (await read.ProviderCommands.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync()).ToHashSet();
        Assert.Equal(
            ["old-leased", "old-pending", "todays-failed-backup", "young-done", "young-failed"],
            rows.Where(r => left.Contains(r.Value.Id)).Select(r => r.Key).Order());

        // The schedule's next ask of the day still finds the failed backup and runs it again.
        await using TitanDbContext again = pg.NewDbContext();
        await new ProviderCommandQueue(new ProviderCommandRepository(again), TimeProvider.System)
            .EnqueueVmBackupAsync(backupVm, "vm-x", "t-1", today);
        await again.SaveChangesAsync();
        await using TitanDbContext after = pg.NewDbContext();
        ProviderCommand backup = await after.ProviderCommands.SingleAsync(c => c.Key == backupKey);
        Assert.Equal((ProviderCommandStatus.Pending, 0), (backup.Status, backup.Attempts));
    }
}

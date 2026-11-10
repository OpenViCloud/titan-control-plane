using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Platform.ProviderCommand;
using Titan.Application.Commands.Platform.ProviderCommand.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Endpoints;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops architecture/provider-and-worker.md#provider-command-table on real Postgres: the unique key and the version
// token, which InMemory does not enforce (ProviderCommandSchemaTests pins only their metadata).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ProviderCommandLeaseDbTests(PostgresFixture pg)
{
    private const int Workers = 8;

    private static string UniqueKind() => "test." + Guid.NewGuid().ToString("N");

    private async Task<Guid> SeedAsync(string kind)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ProviderCommand command = ProviderCommand.Create("test-" + Guid.NewGuid(), kind, "mock", "{}",
            DateTimeOffset.UtcNow.AddSeconds(-1));
        db.ProviderCommands.Add(command);
        await db.SaveChangesAsync();
        return command.Id;
    }

    // Eight workers lease at once and see the same single row: exactly one lease may commit, the others stay empty.
    // Drop IsConcurrencyToken() from the mapping and several leases "win" the same row (the last write keeps its id).
    [Fact]
    public async Task ConcurrentLeases_OfOneRow_ExactlyOneHoldsIt()
    {
        string kind = UniqueKind();
        Guid commandId = await SeedAsync(kind);
        Guid[] leaseIds = Enumerable.Range(0, Workers).Select(_ => Guid.NewGuid()).ToArray();
        using var start = new SemaphoreSlim(0, Workers);

        Task[] leases = leaseIds.Select(async leaseId =>
        {
            await using TitanDbContext db = pg.NewDbContext();
            var handler = new LeaseProviderCommandCommandHandler(new ProviderCommandRepository(db), TimeProvider.System);
            await start.WaitAsync();
            await handler.HandleAsync(new LeaseProviderCommandCommand(leaseId, [kind]));
        }).ToArray();
        start.Release(Workers);
        await Task.WhenAll(leases);

        await using TitanDbContext read = pg.NewDbContext();
        ProviderCommand row = await read.ProviderCommands.AsNoTracking().SingleAsync(c => c.Id == commandId);
        Assert.Equal(ProviderCommandStatus.Leased, row.Status);
        Assert.Equal(1, row.Attempts);
        Guid? holder = row.LeaseId;
        Assert.NotNull(holder);
        Assert.Contains(holder.Value, leaseIds);
    }

    // Deterministic form of the race: two workers both read the row as claimable before either commits. The second
    // commit must lose, or both would run the command believing they hold it. Drop IsConcurrencyToken() from the
    // mapping and the second SaveChanges succeeds.
    [Fact]
    public async Task SecondLease_OfARowReadBeforeTheFirstCommitted_Loses()
    {
        string kind = UniqueKind();
        await SeedAsync(kind);
        await using TitanDbContext dbA = pg.NewDbContext();
        await using TitanDbContext dbB = pg.NewDbContext();
        var repoA = new ProviderCommandRepository(dbA);
        var repoB = new ProviderCommandRepository(dbB);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProviderCommand seenByA = (await repoA.ListClaimableAsync([kind], now, 1, CancellationToken.None))[0];
        ProviderCommand seenByB = (await repoB.ListClaimableAsync([kind], now, 1, CancellationToken.None))[0];

        seenByA.Lease(Guid.NewGuid(), now);
        await repoA.SaveChangesAsync(CancellationToken.None);
        seenByB.Lease(Guid.NewGuid(), now);

        ConcurrencyConflictException ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => repoB.SaveChangesAsync(CancellationToken.None));
        Assert.Equal("PROVIDER_COMMAND_CONFLICT", ex.Code);
    }

    private static readonly DateOnly BackupDay = new(2026, 11, 3);

    // Two handlers enqueue the same key in two transactions: one row survives, the loser hits 23505 and answers 409.
    // A VM's backup of one day: its key is still the resource's (the keys of a resource on the request lifecycle carry the
    // request id, titan-ops architecture/resource-lifecycle.md#one-key-per-request, so two of them never share a key).
    [Fact]
    public async Task SameKey_FromTwoScopes_LeavesOneRow()
    {
        global::Titan.Domain.Aggregates.Vm.Vm backupVm =
            global::Titan.Domain.Aggregates.Vm.Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "vm-x", "m", "tpl", "mock", 20);
        Guid vmId = backupVm.Id;
        string key = $"vm-backup-{vmId}-20261103";

        await using (TitanDbContext first = pg.NewDbContext())
        {
            await new ProviderCommandQueue(new ProviderCommandRepository(first), TimeProvider.System)
                .EnqueueVmBackupAsync(backupVm, "vm-x", "t-1", BackupDay);
            await first.SaveChangesAsync();
        }
        await using (TitanDbContext second = pg.NewDbContext())
        {
            second.ProviderCommands.Add(ProviderCommand.Create(key, ProviderCommandKinds.VmBackup, "mock", "{}", DateTimeOffset.UtcNow));
            DbUpdateException clash = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
            // Two schedulers at once reach the caller as "in progress", not as a 500.
            ErrorStatus status = ErrorStatus.For(clash);
            Assert.Equal(409, status.Status);
            Assert.Equal(ErrorStatus.RequestInProgressCode, status.Code);
        }

        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(1, await read.ProviderCommands.CountAsync(c => c.Key == key));
    }

    // Two identical repeats at the same instant both reopen one Failed row (a failed backup of the day, asked twice at
    // once): the version token lets one commit; the other reaches the caller as 409, not 500. A backup, not a resource's
    // request: those keys are never reused (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task TwoConcurrentReopens_OfOneFailedRow_TheSecondIs409()
    {
        global::Titan.Domain.Aggregates.Vm.Vm backupVm =
            global::Titan.Domain.Aggregates.Vm.Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "vm-x", "m", "tpl", "mock", 20);
        Guid vmId = backupVm.Id;
        Guid leaseId = Guid.NewGuid();
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            ProviderCommand failed = ProviderCommand.Create($"vm-backup-{vmId}-20261103", ProviderCommandKinds.VmBackup, "mock",
                "{}", DateTimeOffset.UtcNow);
            failed.Lease(leaseId, DateTimeOffset.UtcNow);
            failed.Fail(leaseId, "gave up", retryable: false, DateTimeOffset.UtcNow);
            seed.ProviderCommands.Add(failed);
            await seed.SaveChangesAsync();
        }
        await using TitanDbContext dbA = pg.NewDbContext();
        await using TitanDbContext dbB = pg.NewDbContext();
        var queueA = new ProviderCommandQueue(new ProviderCommandRepository(dbA), TimeProvider.System);
        var queueB = new ProviderCommandQueue(new ProviderCommandRepository(dbB), TimeProvider.System);

        await queueA.EnqueueVmBackupAsync(backupVm, "vm-x", "t-1", BackupDay);
        await queueB.EnqueueVmBackupAsync(backupVm, "vm-x", "t-1", BackupDay);
        await dbA.SaveChangesAsync();
        DbUpdateConcurrencyException lost = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());

        ErrorStatus status = ErrorStatus.For(lost);
        Assert.Equal(409, status.Status);
        Assert.Equal(ErrorStatus.RequestInProgressCode, status.Code);
    }
}

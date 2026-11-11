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

// titan-ops architecture/provider-and-worker.md#provider-command-table on real Postgres: the unique key, the version
// token and the indexes on leased rows, which InMemory does not enforce (ProviderCommandSchemaTests pins only their
// metadata), and the lane test in the SQL EF writes.
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

    private async Task<Guid> SeedForAsync(string kind, Guid? tenantId, DateTimeOffset createdAt)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ProviderCommand command = ProviderCommand.Create("test-" + Guid.NewGuid(), kind, "mock", "{}", createdAt, tenantId);
        db.ProviderCommands.Add(command);
        await db.SaveChangesAsync();
        return command.Id;
    }

    // titan-ops features/provider/user-stories.md#p3-2 AC-2: two leases read at once while the only waiting rows are one
    // tenant's; each picks a different row, and only one may commit. The version token sees two rows and stops neither:
    // on a schema without the leased-per-tenant index both commit.
    [Fact]
    public async Task TwoLeasesReadingAtOnce_TheRowsOfOneTenant_OnlyOneCommits()
    {
        string kind = UniqueKind();
        Guid tenant = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await SeedForAsync(kind, tenant, now.AddSeconds(-2));
        await SeedForAsync(kind, tenant, now.AddSeconds(-1));
        await using TitanDbContext dbA = pg.NewDbContext();
        await using TitanDbContext dbB = pg.NewDbContext();
        var repoA = new ProviderCommandRepository(dbA);
        var repoB = new ProviderCommandRepository(dbB);
        ProviderCommand seenByA = (await repoA.ListClaimableAsync([kind], now, 2, CancellationToken.None))[0];
        ProviderCommand seenByB = (await repoB.ListClaimableAsync([kind], now, 2, CancellationToken.None))[1];

        seenByA.Lease(Guid.NewGuid(), now);
        await repoA.SaveChangesAsync(CancellationToken.None);
        seenByB.Lease(Guid.NewGuid(), now);

        ConcurrencyConflictException ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => repoB.SaveChangesAsync(CancellationToken.None));
        Assert.Equal("PROVIDER_COMMAND_CONFLICT", ex.Code);
        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(1, await read.ProviderCommands.CountAsync(c => c.Kind == kind && c.Status == ProviderCommandStatus.Leased));
    }

    // The same race over two rows of no tenant of one kind (template.sync's lane): the leased-no-tenant index lets one
    // commit.
    [Fact]
    public async Task TwoLeasesReadingAtOnce_TwoRowsOfNoTenant_OnlyOneCommits()
    {
        string kind = UniqueKind();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await SeedForAsync(kind, null, now.AddSeconds(-2));
        await SeedForAsync(kind, null, now.AddSeconds(-1));
        await using TitanDbContext dbA = pg.NewDbContext();
        await using TitanDbContext dbB = pg.NewDbContext();
        var repoA = new ProviderCommandRepository(dbA);
        var repoB = new ProviderCommandRepository(dbB);
        ProviderCommand seenByA = (await repoA.ListClaimableAsync([kind], now, 2, CancellationToken.None))[0];
        ProviderCommand seenByB = (await repoB.ListClaimableAsync([kind], now, 2, CancellationToken.None))[1];

        seenByA.Lease(Guid.NewGuid(), now);
        await repoA.SaveChangesAsync(CancellationToken.None);
        seenByB.Lease(Guid.NewGuid(), now);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repoB.SaveChangesAsync(CancellationToken.None));
        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(1, await read.ProviderCommands.CountAsync(c => c.Kind == kind && c.Status == ProviderCommandStatus.Leased));
    }

    // Eight leases through the handler at once on one tenant's four rows: one row is leased, the rest wait. Each lease
    // takes one candidate, the oldest, so here the version token decides; the race over two rows is the test above.
    [Fact]
    public async Task ConcurrentLeases_OfOneTenantsRows_LeaseOne()
    {
        string kind = UniqueKind();
        Guid tenant = Guid.NewGuid();
        for (int i = 0; i < 4; i++)
        {
            await SeedForAsync(kind, tenant, DateTimeOffset.UtcNow.AddSeconds(-10 + i));
        }
        using var start = new SemaphoreSlim(0, Workers);

        Task[] leases = Enumerable.Range(0, Workers).Select(async _ =>
        {
            await using TitanDbContext db = pg.NewDbContext();
            var handler = new LeaseProviderCommandCommandHandler(new ProviderCommandRepository(db), TimeProvider.System);
            await start.WaitAsync();
            await handler.HandleAsync(new LeaseProviderCommandCommand(Guid.NewGuid(), [kind]));
        }).ToArray();
        start.Release(Workers);
        await Task.WhenAll(leases);

        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(1, await read.ProviderCommands.CountAsync(c => c.Kind == kind && c.Status == ProviderCommandStatus.Leased));
    }

    // titan-ops features/provider/user-stories.md#p3-1 and #p3-2 on the SQL EF writes: a busy tenant is skipped, the next
    // tenant and a row of no tenant are handed out; a second row of no tenant waits for the first (the nullable
    // comparison of the lane).
    [Fact]
    public async Task ABusyTenant_IsSkipped_AndTheNoTenantLaneTakesOneRowAtATime()
    {
        string kind = UniqueKind();
        Guid busy = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await SeedForAsync(kind, busy, now.AddSeconds(-5));
        Guid busyWaiting = await SeedForAsync(kind, busy, now.AddSeconds(-4));
        Guid noTenant = await SeedForAsync(kind, null, now.AddSeconds(-3));
        Guid noTenantWaiting = await SeedForAsync(kind, null, now.AddSeconds(-2));
        Guid other = await SeedForAsync(kind, Guid.NewGuid(), now.AddSeconds(-1));

        for (int i = 0; i < 4; i++)
        {
            await using TitanDbContext db = pg.NewDbContext();
            await new LeaseProviderCommandCommandHandler(new ProviderCommandRepository(db), TimeProvider.System)
                .HandleAsync(new LeaseProviderCommandCommand(Guid.NewGuid(), [kind]));
        }

        await using TitanDbContext read = pg.NewDbContext();
        Dictionary<Guid, ProviderCommandStatus> status = await read.ProviderCommands.AsNoTracking()
            .Where(c => c.Kind == kind).ToDictionaryAsync(c => c.Id, c => c.Status);
        Assert.Equal(ProviderCommandStatus.Pending, status[busyWaiting]);
        Assert.Equal(ProviderCommandStatus.Leased, status[noTenant]);
        Assert.Equal(ProviderCommandStatus.Pending, status[noTenantWaiting]);
        Assert.Equal(ProviderCommandStatus.Leased, status[other]);
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

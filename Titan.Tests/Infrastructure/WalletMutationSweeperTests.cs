using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Domain.Repositories.Billing;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — what the sweeper does when another writer moves a
// mutation between the sweeper's read and its write. Both cases need a hook between those two steps, which only a
// test double gives (titan-ops architecture/testing.md#doubles-only-in-unit-tests), so they run on the InMemory provider:
// it enforces concurrency tokens but no unique index, so a mutation applied twice shows up as money moved twice.
// The list is LINQ here, not the production FOR UPDATE SKIP LOCKED (WalletMutationSweeperDbTests covers that).
public class WalletMutationSweeperTests
{
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _name = "sweeper-" + Guid.NewGuid();

    private TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseInMemoryDatabase(_name, _root).Options);

    private static WalletMutationSweeper Sweeper(IServiceProvider root) =>
        new(root.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WalletSweeperOptions { StaleAfterSeconds = 10, MaxAttempts = 5, BatchSize = 100 }),
            new WalletSweeperHeartbeat(),
            NullLogger<WalletMutationSweeper>.Instance);

    private ServiceCollection RealRepositories()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb());
        services.AddScoped<IWalletMutationRepository>(sp => new WalletMutationRepository(sp.GetRequiredService<TitanDbContext>()));
        services.AddScoped<IInvoiceRepository>(sp => new InvoiceRepository(sp.GetRequiredService<TitanDbContext>()));
        return services;
    }

    private static async Task<int> SweepAsync(WalletMutationSweeper sweeper, IServiceProvider root)
    {
        using var pass = root.CreateScope();
        var listed = new ListsPendingThroughLinq(pass.ServiceProvider.GetRequiredService<TitanDbContext>());
        return await sweeper.SweepOnceAsync(listed, default);
    }

    private async Task<(Wallet Wallet, WalletMutation First, WalletMutation Second)> SeedAsync()
    {
        var wallet = Wallet.Open(Guid.NewGuid(), "VND");
        var first = WalletMutation.ForCredit(wallet.Id, 100_000L, "TopUp", Guid.NewGuid());
        var second = WalletMutation.ForCredit(wallet.Id, 50_000L, "TopUp", Guid.NewGuid());
        await using var db = NewDb();
        db.Wallets.Add(wallet);
        db.WalletMutations.Add(first);
        db.WalletMutations.Add(second);
        await db.SaveChangesAsync();
        return (wallet, first, second);
    }

    private async Task<(long Balance, long LedgerSum)> MoneyAsync(Guid walletId)
    {
        await using var db = NewDb();
        long balance = (await db.Wallets.AsNoTracking().SingleAsync(w => w.Id == walletId)).AvailableAmount;
        long ledgerSum = await db.WalletTransactions.AsNoTracking().Where(t => t.WalletId == walletId).SumAsync(t => t.Amount);
        return (balance, ledgerSum);
    }

    // The bus subscriber applies the first mutation after the sweeper listed it and before the sweeper reaches it.
    // The list's context still holds it as Pending; the apply must not go through that context. The false case is
    // the harness self-check: with nobody else applying, the same wiring moves the money once.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sweep_MutationAppliedElsewhereAfterTheList_MovesTheMoneyOnce(bool subscriberWinsFirst)
    {
        var (wallet, first, _) = await SeedAsync();
        var services = RealRepositories();
        services.AddScoped<IWalletRepository>(sp => new WalletRepository(sp.GetRequiredService<TitanDbContext>()));
        services.AddScoped<WalletMutationConsumer>();
        Guid appliedElsewhere = Guid.Empty;
        if (subscriberWinsFirst)
        {
            appliedElsewhere = first.Id;
        }
        services.AddScoped<IWalletMutationConsumer>(sp => new SubscriberWinsFirstApply(
            sp.GetRequiredService<WalletMutationConsumer>(), sp.GetRequiredService<IServiceScopeFactory>(), appliedElsewhere));
        ServiceProvider root = services.BuildServiceProvider();

        await SweepAsync(Sweeper(root), root);

        var (balance, ledgerSum) = await MoneyAsync(wallet.Id);
        Assert.Equal(150_000L, ledgerSum);
        Assert.Equal(ledgerSum, balance);
    }

    // The status token refuses to record a failed attempt on a row whose status moved between the sweeper's read
    // and its save (the subscriber applied it). That refusal is skipped, not fatal: the rest of the batch runs.
    [Fact]
    public async Task Sweep_RowAppliedWhileAFailedAttemptIsRecorded_SkipsIt_AndSweepsTheRest()
    {
        var (wallet, first, second) = await SeedAsync();
        var services = RealRepositories();
        services.AddScoped<IWalletRepository>(sp => new MovesTheRowBeforeEverySave(
            new WalletRepository(sp.GetRequiredService<TitanDbContext>()),
            async () =>
            {
                await using var other = NewDb();
                var row = await other.WalletMutations.SingleAsync(m => m.Id == first.Id);
                if (row.Status == WalletMutationStatus.Pending)
                {
                    row.MarkApplied();
                    await other.SaveChangesAsync();
                }
            }));
        services.AddScoped<IWalletMutationConsumer>(sp => new FailsOn(
            first.Id,
            new WalletMutationConsumer(
                sp.GetRequiredService<IWalletMutationRepository>(),
                new WalletRepository(sp.GetRequiredService<TitanDbContext>()),
                sp.GetRequiredService<IInvoiceRepository>())));
        ServiceProvider root = services.BuildServiceProvider();

        int swept = await SweepAsync(Sweeper(root), root);

        Assert.Equal(2, swept);
        await using var db = NewDb();
        var firstRow = await db.WalletMutations.AsNoTracking().SingleAsync(m => m.Id == first.Id);
        Assert.Equal(WalletMutationStatus.Applied, firstRow.Status);
        Assert.Equal(0, firstRow.Attempts);
        Assert.Equal(WalletMutationStatus.Applied, (await db.WalletMutations.AsNoTracking().SingleAsync(m => m.Id == second.Id)).Status);
        Assert.Equal(50_000L, (await MoneyAsync(wallet.Id)).Balance);
    }

    /// The sweeper's list, through LINQ: every Pending mutation, oldest first, tracked by the pass's context.
    private sealed class ListsPendingThroughLinq(TitanDbContext db) : IWalletMutationRepository
    {
        private readonly WalletMutationRepository _real = new(db);

        public async Task<IReadOnlyList<WalletMutation>> ListPendingOlderThanAsync(
            DateTimeOffset cutoff, int batchSize, CancellationToken ct) =>
            await db.WalletMutations.Where(m => m.Status == WalletMutationStatus.Pending)
                .OrderBy(m => m.CreatedAt).Take(batchSize).ToListAsync(ct);

        public Task<WalletMutation?> GetForUpdateAsync(Guid mutationId, CancellationToken ct) => _real.GetForUpdateAsync(mutationId, ct);
        public void Add(WalletMutation mutation) => _real.Add(mutation);
        public Task<WalletMutation?> GetForRefAsync(string refType, Guid refId, CancellationToken ct) => _real.GetForRefAsync(refType, refId, ct);
        public Task<WalletMutation?> GetForRefForUpdateAsync(string refType, Guid refId, CancellationToken ct) =>
            _real.GetForRefForUpdateAsync(refType, refId, ct);
        public Task<IReadOnlyDictionary<Guid, WalletMutation>> GetForRefsAsync(
            string refType, IReadOnlyCollection<Guid> refIds, CancellationToken ct) => _real.GetForRefsAsync(refType, refIds, ct);
        public Task<WalletMutationBacklog> GetHealthSnapshotAsync(DateTimeOffset now, int maxAttempts, CancellationToken ct) =>
            _real.GetHealthSnapshotAsync(now, maxAttempts, ct);
    }

    /// Before the pass applies the named mutation, applies it through another scope: the NATS subscriber that won.
    private sealed class SubscriberWinsFirstApply(
        IWalletMutationConsumer inner, IServiceScopeFactory scopes, Guid appliedElsewhere) : IWalletMutationConsumer
    {
        public async Task ApplyAsync(Guid mutationId, CancellationToken ct = default)
        {
            if (mutationId == appliedElsewhere)
            {
                using IServiceScope subscriber = scopes.CreateScope();
                await subscriber.ServiceProvider.GetRequiredService<WalletMutationConsumer>().ApplyAsync(mutationId, ct);
            }
            await inner.ApplyAsync(mutationId, ct);
        }
    }

    /// Fails the apply of one named mutation; applies every other one for real.
    private sealed class FailsOn(Guid failing, IWalletMutationConsumer inner) : IWalletMutationConsumer
    {
        public Task ApplyAsync(Guid mutationId, CancellationToken ct = default)
        {
            if (mutationId == failing)
            {
                throw new InvalidOperationException("apply failed (test)");
            }
            return inner.ApplyAsync(mutationId, ct);
        }
    }

    /// Runs the given change from another context just before each save: a writer that commits between this
    /// scope's read and its save.
    private sealed class MovesTheRowBeforeEverySave(IWalletRepository inner, Func<Task> before) : IWalletRepository
    {
        public Task<Wallet?> GetByTenantForUpdateAsync(Guid tenantId, CancellationToken ct) =>
            inner.GetByTenantForUpdateAsync(tenantId, ct);
        public Task<Wallet?> GetByIdForUpdateAsync(Guid walletId, CancellationToken ct) =>
            inner.GetByIdForUpdateAsync(walletId, ct);
        public Task<long> GetBalanceAsync(Guid tenantId, CancellationToken ct) => inner.GetBalanceAsync(tenantId, ct);
        public void Add(Wallet wallet) => inner.Add(wallet);
        public void AddTransaction(WalletTransaction transaction) => inner.AddTransaction(transaction);
        public Task<IReadOnlyList<WalletTransaction>> ListTransactionsByTenantAsync(Guid tenantId, CancellationToken ct) =>
            inner.ListTransactionsByTenantAsync(tenantId, ct);

        public async Task SaveChangesAsync(CancellationToken ct)
        {
            await before();
            await inner.SaveChangesAsync(ct);
        }
    }
}

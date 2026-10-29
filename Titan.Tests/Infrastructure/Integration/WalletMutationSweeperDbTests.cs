using Microsoft.EntityFrameworkCore;
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

namespace Titan.Tests.Infrastructure.Integration;

/// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the sweeper, the component that makes a
/// NON-PERSISTENT bus safe.
///
/// Sweep_PendingNeverPublished_IsApplied is the test the whole design rests on: a mutation that was never
/// announced to anybody still gets applied. Without it, "NATS core has no persistence" would mean money
/// quietly not arriving.
///
/// These are Category=Integration and could not be anything else: the drain is raw SQL with
/// FOR UPDATE SKIP LOCKED, which the InMemory provider cannot execute at all. A green unit suite would
/// be silent about every line of it.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletMutationSweeperDbTests(PostgresFixture pg)
{
    /// A sweeper wired to a scope factory that hands out repositories over this fixture's database.
    private (WalletMutationSweeper Sweeper, IServiceProvider Services) NewSweeper(
        int staleAfterSeconds = 10, int maxAttempts = 5)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => pg.NewDbContext());
        services.AddScoped<IWalletMutationRepository>(sp =>
            new WalletMutationRepository(sp.GetRequiredService<TitanDbContext>()));
        services.AddScoped<IWalletRepository>(sp =>
            new WalletRepository(sp.GetRequiredService<TitanDbContext>()));
        services.AddScoped<IInvoiceRepository>(sp =>
            new InvoiceRepository(sp.GetRequiredService<TitanDbContext>()));

        services.AddScoped<IWalletMutationConsumer, WalletMutationConsumer>();

        var provider = services.BuildServiceProvider();
        var options = Options.Create(new WalletSweeperOptions
        {
            StaleAfterSeconds = staleAfterSeconds,
            MaxAttempts = maxAttempts,
            BatchSize = 100
        });

        return (new WalletMutationSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            new WalletSweeperHeartbeat(),
            NullLogger<WalletMutationSweeper>.Instance), provider);
    }

    /// One sweep through a FRESH DI scope, which is what the sweeper's own loop does every tick.
    /// Resolving the scoped repository/consumer off the root provider instead would give each call a
    /// different lifetime than production and quietly change what these tests measure.
    private static async Task<int> SweepAsync(WalletMutationSweeper sweeper, IServiceProvider root)
    {
        using var scope = root.CreateScope();
        return await sweeper.SweepOnceAsync(
            scope.ServiceProvider.GetRequiredService<IWalletMutationRepository>(), default);
    }

    private async Task<Wallet> SeedWalletAsync(Guid tenantId, long balance = 0)
    {
        var wallet = Wallet.Open(tenantId, "VND");
        if (balance > 0) wallet.Credit(balance);

        await using var db = pg.NewDbContext();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        return wallet;
    }

    /// Enqueues a mutation and, when asked, back-dates it so a sweep will consider it stale. Age is set
    /// EXPLICITLY rather than by sleeping: the thing under test is a cutoff comparison, so the quantity
    /// that decides the outcome must be stated, not inherited from how fast the test ran.
    private async Task<WalletMutation> EnqueueAsync(
        WalletMutation mutation, TimeSpan? age = null)
    {
        await using var db = pg.NewDbContext();
        db.WalletMutations.Add(mutation);
        await db.SaveChangesAsync();

        if (age is not null)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE core.wallet_mutations SET created_at = {DateTimeOffset.UtcNow - age.Value}, queued_at = {DateTimeOffset.UtcNow - age.Value} WHERE id = {mutation.Id}");
        }

        return mutation;
    }

    private async Task<WalletMutation> ReadMutationAsync(Guid id)
    {
        await using var db = pg.NewDbContext();
        return await db.WalletMutations.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private async Task<(long Balance, int Movements)> WalletStateAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(w => w.TenantId == tenantId);
        var movements = await db.WalletTransactions.AsNoTracking().CountAsync(t => t.WalletId == wallet.Id);
        return (wallet.AvailableAmount, movements);
    }

    // ⭐ A mutation NOBODY was told about still gets applied. This is the proof that the
    // non-persistent bus cannot lose money: nothing here publishes, subscribes, or notifies.
    [Fact]
    public async Task Sweep_PendingNeverPublished_IsApplied()
    {
        var tenantId = Guid.NewGuid();
        var wallet = await SeedWalletAsync(tenantId);
        var mutation = await EnqueueAsync(
            WalletMutation.ForCredit(wallet.Id, 250_000L, "TopUp", Guid.NewGuid()),
            age: TimeSpan.FromMinutes(1));

        var (sweeper, services) = NewSweeper();
        var swept = await SweepAsync(sweeper, services);

        Assert.Equal(1, swept);
        Assert.Equal(WalletMutationStatus.Applied, (await ReadMutationAsync(mutation.Id)).Status);

        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(250_000L, balance);
        Assert.Equal(1, movements);
    }

    // A mutation younger than the cutoff is LEFT ALONE. Without this, the sweeper would race the
    // subscriber for every mutation the instant it is written, putting two writers on one wallet — the
    // exact condition titan-ops architecture/money-and-wallet.md#single-writer-outbox removes. The symmetric
    // partner of the test above: on its own, that one passes just as well for a sweeper that grabs everything.
    [Fact]
    public async Task Sweep_FreshMutation_IsLeftAlone()
    {
        var tenantId = Guid.NewGuid();
        var wallet = await SeedWalletAsync(tenantId);
        var mutation = await EnqueueAsync(
            WalletMutation.ForCredit(wallet.Id, 250_000L, "TopUp", Guid.NewGuid()));  // age: now

        var (sweeper, services) = NewSweeper(staleAfterSeconds: 60);
        var swept = await SweepAsync(sweeper, services);

        Assert.Equal(0, swept);
        Assert.Equal(WalletMutationStatus.Pending, (await ReadMutationAsync(mutation.Id)).Status);
        Assert.Equal(0L, (await WalletStateAsync(tenantId)).Balance);
    }

    // An already-applied mutation is not swept a second time. The other symmetric partner: it fails if
    // the drain ever loses its `status = 'Pending'` filter, which is precisely the mutation a C#-side
    // status check would absorb silently.
    [Fact]
    public async Task Sweep_AppliedMutation_IsNotReapplied()
    {
        var tenantId = Guid.NewGuid();
        var wallet = await SeedWalletAsync(tenantId);
        var mutation = await EnqueueAsync(
            WalletMutation.ForCredit(wallet.Id, 250_000L, "TopUp", Guid.NewGuid()),
            age: TimeSpan.FromMinutes(1));

        var (sweeper, services) = NewSweeper();
        await SweepAsync(sweeper, services);         // first sweep applies it
        var swept = await SweepAsync(sweeper, services);   // second must find nothing

        Assert.Equal(0, swept);
        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(250_000L, balance);      // NOT 500_000 — the money moved exactly once
        Assert.Equal(1, movements);
    }

    // A mutation that cannot be applied is retried, counted, and eventually declared poison
    // rather than swept forever. Retrying an impossible mutation until the end of time hides a stuck
    // payment behind a status that never changes. The real consumer refuses it: it names a wallet that does
    // not exist (wallet_id has no foreign key, so nothing stops such a row from landing).
    [Fact]
    public async Task Sweep_PoisonMutation_PastMaxAttempts_MarkedFailed()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId);
        var mutation = await EnqueueAsync(
            WalletMutation.ForCredit(Guid.NewGuid(), 250_000L, "TopUp", Guid.NewGuid()),
            age: TimeSpan.FromMinutes(1));

        var (sweeper, services) = NewSweeper(maxAttempts: 2);

        await SweepAsync(sweeper, services);
        var afterOne = await ReadMutationAsync(mutation.Id);
        Assert.Equal(1, afterOne.Attempts);
        Assert.Equal(WalletMutationStatus.Pending, afterOne.Status);   // one bad try is a retry

        await SweepAsync(sweeper, services);
        var afterTwo = await ReadMutationAsync(mutation.Id);
        Assert.Equal(2, afterTwo.Attempts);
        Assert.Equal(WalletMutationStatus.Failed, afterTwo.Status);    // ...two is a verdict
        Assert.Contains("Given up after 2 attempts", afterTwo.LastError!);

        // And once it is dead it stops consuming sweeps.
        Assert.Equal(0, await SweepAsync(sweeper, services));
        Assert.Equal(0L, (await WalletStateAsync(tenantId)).Balance);
    }
}

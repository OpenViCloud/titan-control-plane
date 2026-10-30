using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the backlog query behind /healthz/wallet, on real Postgres.
//
// Why this cannot be a unit test: the query aggregates with GroupBy(_ => 1) + Count + Min so the
// database does the work rather than pulling every pending row back. That shape is exactly the kind
// EF can translate differently — or refuse to translate and silently evaluate client-side — and the
// InMemory provider would answer either way without complaint. A green unit suite would prove
// nothing about what runs on the cluster.
//
// The EMPTY case matters as much as the populated one: GroupBy over an empty set yields no group at
// all, so a naive read returns null and the endpoint would fail on the quietest system in the
// product — which is its normal state.
//
// The fixture's database is shared across the collection, so every test here clears the mutation
// table first and asserts on figures it seeded itself. Counting global rows would make these tests
// depend on what ran before them.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletMutationHealthSnapshotDbTests(PostgresFixture pg)
{
    private const int MaxAttempts = 5;

    private async Task ClearAsync()
    {
        await using var db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM core.wallet_mutations");
    }

    /// Seeds one mutation and, when an age is given, back-dates it with SQL. Stated explicitly rather
    /// than by sleeping: the quantity that decides the outcome must not be inherited from how fast
    /// the test happened to run.
    private async Task SeedAsync(WalletMutation mutation, TimeSpan? age = null)
    {
        await using var db = pg.NewDbContext();
        db.WalletMutations.Add(mutation);
        await db.SaveChangesAsync();

        if (age is not null)
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE core.wallet_mutations SET created_at = {DateTimeOffset.UtcNow - age.Value}, queued_at = {DateTimeOffset.UtcNow - age.Value} WHERE id = {mutation.Id}");
        }
    }

    private async Task<WalletMutationBacklog> SnapshotAsync()
    {
        await using var db = pg.NewDbContext();
        var repo = new WalletMutationRepository(db);
        return await repo.GetHealthSnapshotAsync(DateTimeOffset.UtcNow, MaxAttempts, default);
    }

    // The case a naive implementation gets wrong: no pending rows means no
    // group, and reading a missing group as a row throws — on the system that is working fine.
    [Fact]
    public async Task EmptyBacklog_ReturnsZerosAndNullAge_DoesNotThrow()
    {
        await ClearAsync();

        var snapshot = await SnapshotAsync();

        Assert.Equal(0, snapshot.PendingCount);
        Assert.Equal(0, snapshot.PoisonCount);
        Assert.Null(snapshot.OldestPendingAge);
    }

    [Fact]
    public async Task OldestPendingAge_IsTheOldest_NotTheNewest()
    {
        await ClearAsync();
        var walletId = Guid.NewGuid();

        // Seeded newest-first on purpose: a query that returned whatever the database handed back
        // first would answer 30s here, and this ordering is what catches it.
        await SeedAsync(WalletMutation.ForCredit(walletId, 1_000L, "TopUp", Guid.NewGuid()),
            age: TimeSpan.FromSeconds(30));
        await SeedAsync(WalletMutation.ForCredit(walletId, 1_000L, "TopUp", Guid.NewGuid()),
            age: TimeSpan.FromSeconds(300));
        await SeedAsync(WalletMutation.ForCredit(walletId, 1_000L, "TopUp", Guid.NewGuid()),
            age: TimeSpan.FromSeconds(90));

        var snapshot = await SnapshotAsync();

        Assert.Equal(3, snapshot.PendingCount);
        Assert.NotNull(snapshot.OldestPendingAge);
        // Wide window on purpose: the claim is that it picked the 300s row, not the 30s or 90s one.
        Assert.InRange(snapshot.OldestPendingAge!.Value.TotalSeconds, 290, 400);
    }

    // On real data — the distinction the whole check rests on.
    [Fact]
    public async Task Poison_CountsAttemptsExhausted_NotBusinessFailures()
    {
        await ClearAsync();
        var walletId = Guid.NewGuid();

        // A business failure: the consumer's re-check found the wallet short (titan-ops architecture/money-and-wallet.md#single-writer-outbox). MarkFailed
        // is called directly, so Attempts stays 0. Nobody should be paged for this.
        var shortfall = WalletMutation.ForDebit(walletId, 500L, "Invoice", Guid.NewGuid());
        shortfall.MarkFailed("insufficient funds at apply time");
        await SeedAsync(shortfall);

        // Poison: the sweeper burned through every retry before giving up.
        var poison = WalletMutation.ForDebit(walletId, 700L, "Invoice", Guid.NewGuid());
        for (var i = 0; i < MaxAttempts; i++) poison.RecordAttempt($"attempt {i} failed");
        poison.MarkFailed("given up");
        await SeedAsync(poison);

        var snapshot = await SnapshotAsync();

        // TWO rows are Failed; exactly ONE is poison. Counting status alone would answer 2 and page
        // an operator every time a customer's balance ran short — the failure this test exists for.
        Assert.Equal(1, snapshot.PoisonCount);
        Assert.Equal(0, snapshot.PendingCount);
    }

    // A given-up invoice debit the customer has since paid again is reopened, not stuck
    // (titan-ops features/billing/invoice/design.md#retry-after-failed-debit): Pending with its attempts back at zero,
    // and its pending age counts from the reopen, not from the first press two days ago. A given-up one nobody
    // paid again still pages.
    [Fact]
    public async Task Poison_AReopenedInvoiceDebit_NoLongerCounts_AndIsFreshInTheBacklog()
    {
        await ClearAsync();
        var walletId = Guid.NewGuid();
        var reopenedInvoice = Guid.NewGuid();

        var givenUp = WalletMutation.ForDebit(walletId, 700L, "Invoice", reopenedInvoice);
        for (var i = 0; i < MaxAttempts; i++) givenUp.RecordAttempt($"attempt {i} failed");
        givenUp.MarkFailed("given up");
        await SeedAsync(givenUp, age: TimeSpan.FromDays(2));
        await using (var db = pg.NewDbContext())
        {
            var row = await new WalletMutationRepository(db)
                .GetForRefForUpdateAsync("Invoice", reopenedInvoice, default);
            row!.ReopenFailedDebit();
            await db.SaveChangesAsync();
        }

        var stillStuck = WalletMutation.ForDebit(walletId, 900L, "Invoice", Guid.NewGuid());
        for (var i = 0; i < MaxAttempts; i++) stillStuck.RecordAttempt($"attempt {i} failed");
        stillStuck.MarkFailed("given up");
        await SeedAsync(stillStuck);

        var snapshot = await SnapshotAsync();

        Assert.Equal(1, snapshot.PoisonCount);
        Assert.Equal(1, snapshot.PendingCount);
        Assert.True(snapshot.OldestPendingAge < TimeSpan.FromMinutes(1),
            $"oldest pending age {snapshot.OldestPendingAge} counts from the first press, not the reopen");
    }

    [Fact]
    public async Task AppliedMutations_DoNotCountAsBacklog()
    {
        await ClearAsync();

        var applied = WalletMutation.ForCredit(Guid.NewGuid(), 1_000L, "TopUp", Guid.NewGuid());
        applied.MarkApplied();
        await SeedAsync(applied, age: TimeSpan.FromHours(2));

        var snapshot = await SnapshotAsync();

        // A two-hour-old APPLIED mutation is finished work, not backlog. Reading it as pending would
        // leave the endpoint permanently unhealthy on any system that has ever taken a payment.
        Assert.Equal(0, snapshot.PendingCount);
        Assert.Null(snapshot.OldestPendingAge);
    }
}

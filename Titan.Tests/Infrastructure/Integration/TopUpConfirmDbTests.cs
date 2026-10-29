using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.TopUp;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Aggregates.Billing.TopUp;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — confirm DEFERS the credit: ConfirmAsync writes
// ONE credit mutation in the same transaction as the Confirmed status and publishes a post-commit notify; the
// single-writer consumer applies it. These tests assert on real Postgres: a confirm enqueues exactly one
// mutation and credits nothing yet; the consumer then credits exactly once; two concurrent confirms leave
// exactly one mutation (the mutation's UNIQUE(ref_type,ref_id), not the wallet version, is the guard).
// The consumer's own idempotency + version token are covered by WalletMutationConsumerDbTests.
// Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class TopUpConfirmDbTests(PostgresFixture pg)
{
    // The confirm rule both channels share (TopUpConfirmation), over real repositories. It returns nothing
    // (titan-ops architecture/http-contract.md#commands-return-no-data), so these tests read the stored row back
    // through a fresh context — the way the admin list sees it.
    private static ConfirmRule NewService(TitanDbContext db, IWalletMutationPublisher bus) => new(db, bus);

    private sealed class ConfirmRule(TitanDbContext db, IWalletMutationPublisher bus)
    {
        public Task ConfirmAsync(
            Guid topUpId, TopUpSource source, string confirmedBy, string? paymentTxnRef, DateTimeOffset? confirmedAt = null)
            => TopUpConfirmation.ConfirmAsync(
                new CreditRepository(db), new WalletRepository(db), new WalletMutationRepository(db), bus,
                topUpId, source, confirmedBy, paymentTxnRef, confirmedAt, CancellationToken.None);
    }

    private async Task<TopUp> StoredAsync(Guid topUpId)
    {
        await using var check = pg.NewDbContext();
        return await check.TopUps.AsNoTracking().SingleAsync(t => t.Id == topUpId);
    }

    private async Task<TopUp> SeedPendingTopUpAsync(Guid tenantId, decimal amount)
    {
        var topUp = TopUp.Create(tenantId, (long)amount, "VND");
        await using var db = pg.NewDbContext();
        db.TopUps.Add(topUp);
        await db.SaveChangesAsync();
        return topUp;
    }

    private async Task SeedWalletAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        db.Wallets.Add(Wallet.Open(tenantId, "VND"));
        await db.SaveChangesAsync();
    }

    // The credit mutation(s) a top-up produced — the idempotency assertion surface.
    private async Task<List<WalletMutation>> MutationsForAsync(Guid topUpId)
    {
        await using var db = pg.NewDbContext();
        return await db.WalletMutations.AsNoTracking()
            .Where(c => c.RefType == "TopUp" && c.RefId == topUpId).ToListAsync();
    }

    private async Task<(long balance, int movements)> WalletStateAsync(Guid tenantId, Guid topUpId)
    {
        await using var db = pg.NewDbContext();
        var movements = await db.WalletTransactions.AsNoTracking().CountAsync(t => t.RefId == topUpId);
        var balance = await new WalletRepository(db).GetBalanceAsync(tenantId, default);
        return (balance, movements);
    }

    // Applies a mutation exactly as the single-writer consumer would (drain, no bus).
    private async Task DrainAsync(Guid mutationId)
    {
        await using var db = pg.NewDbContext();
        await new WalletMutationConsumer(new WalletMutationRepository(db), new WalletRepository(db), new InvoiceRepository(db))
            .ApplyAsync(mutationId);
    }

    // Confirming a Pending top-up ENQUEUES exactly one Pending credit mutation and credits NOTHING yet
    // (the credit is the consumer's job). The post-commit notify fires for that mutation.
    [Fact]
    public async Task Confirm_EnqueuesOneCreditMutation_DoesNotCreditYet()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId);
        var topUp = await SeedPendingTopUpAsync(tenantId, 100m);
        var bus = WalletBus.None();

        await using (var db = pg.NewDbContext())
            await NewService(db, bus).ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin-1", "BANK-1");

        Assert.Equal(TopUpStatus.Confirmed, (await StoredAsync(topUp.Id)).Status);

        var mutations = await MutationsForAsync(topUp.Id);
        Assert.Single(mutations);                                    // exactly one — not zero, not two
        Assert.Equal(WalletMutationType.Credit, mutations[0].Type);
        Assert.Equal(100L, mutations[0].Amount);
        Assert.Equal(WalletMutationStatus.Pending, mutations[0].Status);

        var (balance, movements) = await WalletStateAsync(tenantId, topUp.Id);
        Assert.Equal(0L, balance);       // NOT credited in the request — the credit is deferred
        Assert.Equal(0, movements);      // no ledger row yet

    }

    // End-to-end: after the consumer applies the mutation, the wallet is credited EXACTLY ONCE,
    // and a redelivery credits nothing more (the consumer's idempotency, proven through the confirm path).
    [Fact]
    public async Task Confirm_ThenConsumerApplies_CreditsWalletExactlyOnce()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId);
        var topUp = await SeedPendingTopUpAsync(tenantId, 100m);
        var bus = WalletBus.None();

        await using (var db = pg.NewDbContext())
            await NewService(db, bus).ConfirmAsync(topUp.Id, TopUpSource.SePay, "sys", "txn-1");

        var mutation = Assert.Single(await MutationsForAsync(topUp.Id));
        await DrainAsync(mutation.Id);

        var (balance, movements) = await WalletStateAsync(tenantId, topUp.Id);
        Assert.Equal(100L, balance);
        Assert.Equal(1, movements);

        await DrainAsync(mutation.Id);    // redelivery — no-op
        var (balance2, movements2) = await WalletStateAsync(tenantId, topUp.Id);
        Assert.Equal(100L, balance2);
        Assert.Equal(1, movements2);
    }

    // Money in is not money spent (titan-ops architecture/money-and-wallet.md#human-confirmed-debit): a top-up that lands
    // while the tenant owes an invoice credits the wallet in full and leaves the invoice owed, until someone
    // presses pay for it.
    [Fact]
    public async Task Confirm_WhileAnInvoiceIsOwed_CreditsTheWallet_AndLeavesTheInvoiceOwed()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId);
        var owed = Invoice.Generate(tenantId, 2026, 1, DateTimeOffset.UtcNow.AddDays(-10),
            [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "vm-1", 60L, "VND")], "VND");
        await using (var seed = pg.NewDbContext())
        {
            seed.Invoices.Add(owed);
            await seed.SaveChangesAsync();
        }
        var topUp = await SeedPendingTopUpAsync(tenantId, 100m);

        await using (var db = pg.NewDbContext())
            await NewService(db, WalletBus.None()).ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin-1", "BANK-2");
        await DrainAsync(Assert.Single(await MutationsForAsync(topUp.Id)).Id);

        var (balance, movements) = await WalletStateAsync(tenantId, topUp.Id);
        Assert.Equal(100L, balance);     // the whole top-up, nothing taken for the invoice
        Assert.Equal(1, movements);
        await using var check = pg.NewDbContext();
        Invoice stored = await check.Invoices.AsNoTracking().SingleAsync(i => i.Id == owed.Id);
        Assert.Equal(InvoiceStatus.New, stored.Status);
        Assert.Null(stored.PaidAt);
    }

    // A retry of an already-confirmed top-up (the fast path) enqueues NO new mutation and
    // does not re-stamp the confirmation. This is the path a real SePay retry takes minutes later.
    [Fact]
    public async Task Confirm_AlreadyConfirmed_EnqueuesNoNewMutation_AndKeepsFirstConfirmation()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId);
        var topUp = await SeedPendingTopUpAsync(tenantId, 100m);
        var bus = WalletBus.None();

        await using (var db = pg.NewDbContext())
            await NewService(db, bus).ConfirmAsync(topUp.Id, TopUpSource.SePay, "sys", "txn-1");
        var first = await StoredAsync(topUp.Id);

        await using (var db = pg.NewDbContext())
            await NewService(db, bus).ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin", "txn-2");
        var retry = await StoredAsync(topUp.Id);

        Assert.Single(await MutationsForAsync(topUp.Id));                 // still one mutation, not two
        Assert.Equal(TopUpSource.SePay, retry.Source);                  // not re-stamped by the retry
        Assert.Equal(first.ConfirmedAt, retry.ConfirmedAt);
    }

    // Two confirms of the same top-up fired at once enqueue EXACTLY ONE mutation. The wallet is never
    // touched here, so there is no wallet-version race to lose; the mutation's UNIQUE(ref_type, ref_id)
    // makes exactly one survive, and the loser succeeds on the winner's confirmation. After the consumer
    // runs, credited once.
    //
    // 🔴 Mutation check: drop `.IsUnique()` from the mutation's (ref_type, ref_id) index and this goes red
    // (two mutations land → double credit after draining). The guard, not the code path, is what holds.
    [Fact]
    public async Task ConcurrentConfirm_SameTopUp_EnqueuesExactlyOneMutation_CreditsOnce()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId);
        var topUp = await SeedPendingTopUpAsync(tenantId, 100m);
        var bus = WalletBus.None();

        await using var dbA = pg.NewDbContext();
        await using var dbB = pg.NewDbContext();
        // Both callers succeed — neither throws (the loser re-read the winner's row and took it as done).
        await Task.WhenAll(
            NewService(dbA, bus).ConfirmAsync(topUp.Id, TopUpSource.SePay, "sys", "txn-A"),
            NewService(dbB, bus).ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin", "txn-B"));
        Assert.Equal(TopUpStatus.Confirmed, (await StoredAsync(topUp.Id)).Status);

        var mutation = Assert.Single(await MutationsForAsync(topUp.Id));  // exactly one survived the race
        await DrainAsync(mutation.Id);

        var (balance, movements) = await WalletStateAsync(tenantId, topUp.Id);
        Assert.Equal(100L, balance);   // credited by 100 once, not 200
        Assert.Equal(1, movements);
    }
}

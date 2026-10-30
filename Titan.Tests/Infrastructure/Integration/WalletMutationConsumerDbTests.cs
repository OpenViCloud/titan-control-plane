using Microsoft.EntityFrameworkCore;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the single-writer consumer on real Postgres.
// These prove what the InMemory provider and the stub-based unit tests (WalletMutationConsumerTests) cannot:
// the apply is atomic (one SaveChanges = one BEGIN…COMMIT), idempotent under redelivery against the DB
// itself, and threads a correct BalanceAfter chain when a wallet's mutations are applied in order.
// Category=Integration — its own CI step; the dev box has no Docker.
//
// Each ApplyAsync uses its own DbContext, mirroring the app's scope-per-unit-of-work (and its race
// windows). Isolation is by fresh Guids per test (PostgresFixture).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletMutationConsumerDbTests(PostgresFixture pg)
{
    private static WalletMutationConsumer NewConsumer(TitanDbContext db) =>
        new(new WalletMutationRepository(db), new WalletRepository(db), new InvoiceRepository(db));

    private async Task<Wallet> SeedWalletAsync(Guid tenantId)
    {
        var wallet = Wallet.Open(tenantId, "VND");
        await using var db = pg.NewDbContext();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        return wallet;
    }

    private async Task EnqueueAsync(WalletMutation mutation)
    {
        await using var db = pg.NewDbContext();
        db.WalletMutations.Add(mutation);
        await db.SaveChangesAsync();
    }

    private async Task<(long balance, int movements)> WalletStateAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(w => w.TenantId == tenantId);
        var movements = await db.WalletTransactions.AsNoTracking().CountAsync(t => t.WalletId == wallet.Id);
        return (wallet.AvailableAmount, movements);
    }

    private async Task<WalletMutationStatus> MutationStatusAsync(Guid mutationId)
    {
        await using var db = pg.NewDbContext();
        return (await db.WalletMutations.AsNoTracking().SingleAsync(c => c.Id == mutationId)).Status;
    }

    // Applying a Pending credit mutation credits the wallet once, records exactly one TopUp
    // movement, and marks the mutation Applied. "One of each, and the mutation says Applied" is what
    // catches a credit whose ledger row or status flip did not commit with the balance.
    [Fact]
    public async Task Apply_CreditMutation_CreditsWalletOnce_RecordsOneMovement_MarksApplied()
    {
        var tenantId = Guid.NewGuid();
        var wallet = await SeedWalletAsync(tenantId);
        var topUpId = Guid.NewGuid();
        var mutation = WalletMutation.ForCredit(wallet.Id, 100_000L, "TopUp", topUpId);
        await EnqueueAsync(mutation);

        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync(mutation.Id);

        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(100_000L, balance);
        Assert.Equal(1, movements);   // non-empty AND exactly one
        Assert.Equal(WalletMutationStatus.Applied, await MutationStatusAsync(mutation.Id));

        await using var verify = pg.NewDbContext();
        var movement = await verify.WalletTransactions.AsNoTracking().SingleAsync(t => t.WalletId == wallet.Id);
        Assert.Equal(WalletTransactionType.TopUp, movement.Type);
        Assert.Equal(topUpId, movement.RefId);
        Assert.Equal(100_000L, movement.BalanceAfter);
    }

    // THE idempotency proof: applying the same mutation twice credits the wallet ONCE. The
    // second call finds the mutation already Applied and no-ops. Balance is the deterministic oracle: it
    // is 100k, not 200k, and there is one movement, not two.
    //
    // 🔴 Mutation check: delete `if (mutation.Status != Pending) return;` from the consumer and the second
    // ApplyAsync tries to credit again → the ledger UNIQUE(type, ref_id) rejects the
    // duplicate movement → SaveChanges throws → this test goes red on the unhandled exception. Remove
    // that unique too and the balance doubles → the movements/balance asserts go red. Either way the
    // no-op is pinned, not assumed.
    [Fact]
    public async Task Apply_SameMutationTwice_CreditsOnce_SecondCallIsNoOp()
    {
        var tenantId = Guid.NewGuid();
        var wallet = await SeedWalletAsync(tenantId);
        var mutation = WalletMutation.ForCredit(wallet.Id, 100_000L, "TopUp", Guid.NewGuid());
        await EnqueueAsync(mutation);

        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync(mutation.Id);   // first delivery — applies
        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync(mutation.Id);   // redelivery — must be a clean no-op

        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(100_000L, balance);   // credited once, not twice
        Assert.Equal(1, movements);        // one ledger row, not two
    }

    // The single writer applies a wallet's mutations in order, each seeing the previous one's effect.
    // Credit 100k then Debit 30k leaves 70k across two movements. The deciding quantity: the InvoicePayment
    // row's BalanceAfter is 70k, which is only possible if the credit was applied FIRST — a debit on an
    // empty wallet would have been marked Failed (insufficient funds), so the order is causal, not
    // incidental to creation timestamps.
    [Fact]
    public async Task Apply_CreditThenDebit_SameWallet_AppliesInOrder_LeavesCorrectBalance()
    {
        var tenantId = Guid.NewGuid();
        var wallet = await SeedWalletAsync(tenantId);
        var credit = WalletMutation.ForCredit(wallet.Id, 100_000L, "TopUp", Guid.NewGuid());
        // A debit pays a real, still-owed invoice; one whose invoice is gone fails without touching the wallet.
        var invoice = Invoice.Generate(tenantId, 2026, 9, DateTimeOffset.UtcNow.AddDays(-1),
            [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "vm-1", 30_000L, "VND")], "VND");
        await using (var seed = pg.NewDbContext())
        {
            seed.Invoices.Add(invoice);
            await seed.SaveChangesAsync();
        }
        var debit = WalletMutation.ForDebit(wallet.Id, 30_000L, "Invoice", invoice.Id);
        await EnqueueAsync(credit);
        await EnqueueAsync(debit);

        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync(credit.Id);
        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync(debit.Id);

        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(70_000L, balance);   // 100k − 30k
        Assert.Equal(2, movements);
        Assert.Equal(WalletMutationStatus.Applied, await MutationStatusAsync(credit.Id));
        Assert.Equal(WalletMutationStatus.Applied, await MutationStatusAsync(debit.Id));

        await using var verify = pg.NewDbContext();
        var topUp = await verify.WalletTransactions.AsNoTracking()
            .SingleAsync(t => t.WalletId == wallet.Id && t.Type == WalletTransactionType.TopUp);
        var payment = await verify.WalletTransactions.AsNoTracking()
            .SingleAsync(t => t.WalletId == wallet.Id && t.Type == WalletTransactionType.InvoicePayment);
        Assert.Equal(100_000L, topUp.BalanceAfter);      // balance after the credit
        Assert.Equal(-30_000L, payment.Amount);          // signed — money out
        Assert.Equal(70_000L, payment.BalanceAfter);     // the debit saw the credit's 100k → 70k
    }
}

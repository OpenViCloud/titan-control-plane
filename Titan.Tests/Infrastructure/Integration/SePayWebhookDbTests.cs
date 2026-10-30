using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.PaymentGatewayTransaction;
using Titan.Application.Commands.Billing.PaymentGatewayTransaction.Contracts;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Wallet;
using Titan.Infrastructure.Repositories;
using Titan.Infrastructure.Persistence;
using Titan.Application.Services.Billing.WalletMutations;

namespace Titan.Tests.Infrastructure.Integration;

// The SePay webhook end-to-end against a real Postgres: a matched payment ENQUEUES exactly one credit
// mutation (titan-ops architecture/money-and-wallet.md#single-writer-outbox — confirm defers the credit), a retried
// webhook (same SePay txn id) is deduped by the payment_transactions PK so no second mutation is enqueued, and
// the confirm is stamped at the payment's transactionDate. Draining the mutation then proves the wallet is
// credited exactly once.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class SePayWebhookDbTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset PaidAt = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    private static ReceiveSePayPaymentCommandHandler NewService(TitanDbContext db, IWalletMutationPublisher bus) =>
        new(new PaymentGatewayTransactionRepository(db), new CreditRepository(db), new WalletRepository(db),
            new WalletMutationRepository(db), bus, NullLogger<ReceiveSePayPaymentCommandHandler>.Instance);

    // Applies a mutation exactly as the single-writer consumer would — drain, no bus.
    private async Task DrainAsync(Guid mutationId)
    {
        await using var db = pg.NewDbContext();
        await new WalletMutationConsumer(new WalletMutationRepository(db), new WalletRepository(db), new InvoiceRepository(db))
            .ApplyAsync(mutationId);
    }

    private async Task<TopUp> SeedPendingTopUpAsync(Guid tenantId, decimal amount)
    {
        var topUp = TopUp.Create(tenantId, (long)amount, "VND");
        await using var db = pg.NewDbContext();
        db.TopUps.Add(topUp);
        await db.SaveChangesAsync();
        return topUp;
    }

    // A wallet exists from tenant activation; the webhook credits it, it does not open it.
    private async Task SeedWalletAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        db.Wallets.Add(Wallet.Open(tenantId, "VND"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Webhook_MatchThenRetry_EnqueuesOneMutation_CreditsOnce_OnePaymentGatewayTransaction()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId);
        var topUp = await SeedPendingTopUpAsync(tenantId, 900000m);
        var txnId = $"sepay-{Guid.NewGuid():N}";
        var payload = new ReceiveSePayPaymentCommand(
            txnId, "Vietcombank", PaidAt, "in", 900000m, $"Nap tien {topUp.PaymentRef} cam on", null);
        var bus = WalletBus.None();

        SePayWebhookOutcome first, second;
        await using (var db = pg.NewDbContext()) first = await NewService(db, bus).HandleAsync(payload);
        await using (var db = pg.NewDbContext()) second = await NewService(db, bus).HandleAsync(payload); // SePay retry

        Assert.Equal(SePayWebhookOutcome.Matched, first);
        Assert.Equal(SePayWebhookOutcome.Duplicate, second); // deduped by payment_transactions PK

        await using var check = pg.NewDbContext();
        // titan-ops architecture/money-and-wallet.md#single-writer-outbox — the webhook DEFERS the credit: exactly one credit mutation enqueued (a double
        // webhook would be 2, a miss 0), and no ledger row until the consumer runs.
        var mutations = await check.WalletMutations.AsNoTracking()
            .Where(c => c.RefType == "TopUp" && c.RefId == topUp.Id).ToListAsync();
        Assert.Single(mutations);
        Assert.Equal(0, await check.WalletTransactions.AsNoTracking().CountAsync(t => t.RefId == topUp.Id));
        Assert.Equal(1, await check.PaymentGatewayTransactions.AsNoTracking().CountAsync(p => p.Id == txnId));

        var confirmed = await check.TopUps.AsNoTracking().SingleAsync(t => t.Id == topUp.Id);
        Assert.Equal(TopUpStatus.Confirmed, confirmed.Status);
        Assert.Equal(TopUpSource.SePay, confirmed.Source);
        Assert.Equal(PaidAt, confirmed.ConfirmedAt);  // payment time, not webhook arrival time

        // Now the single-writer consumer applies it: credited exactly once, one ledger movement.
        await DrainAsync(mutations[0].Id);
        await using var after = pg.NewDbContext();
        Assert.Equal(1, await after.WalletTransactions.AsNoTracking().CountAsync(t => t.RefId == topUp.Id));
        Assert.Equal(900000L, await new WalletRepository(after).GetBalanceAsync(tenantId, default));
    }
}

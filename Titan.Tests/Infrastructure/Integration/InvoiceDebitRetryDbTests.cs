using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Application.Queries.Billing.Invoice;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops features/billing/invoice/design.md#retry-after-failed-debit — an invoice holds exactly one wallet debit
// row, ever. Paying again after that debit failed reopens the same row; it never adds a second one. What only
// Postgres can prove: the unique index refuses a second row whatever the first one's status, and presses that
// race on a reopen leave one row, one ledger line, one charge.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class InvoiceDebitRetryDbTests(PostgresFixture pg)
{
    private const string Currency = "VND";
    private const int RaceIterations = 60;

    private static WalletMutation FailedDebit(Guid walletId, Guid invoiceId, long amount = 100_000L, int attempts = 0)
    {
        var mutation = WalletMutation.ForDebit(walletId, amount, WalletMutationConsumer.InvoiceRefType, invoiceId);
        for (var i = 0; i < attempts; i++)
        {
            mutation.RecordAttempt("transient failure (test)");
        }
        mutation.MarkFailed("Wallet short by 100000 VND (test).");
        return mutation;
    }

    private static void AssertRefIndexRefused(DbUpdateException ex)
    {
        var postgres = Assert.IsType<Npgsql.PostgresException>(ex.InnerException);
        Assert.Equal("23505", postgres.SqlState);
        Assert.Equal("IX_wallet_mutations_ref_type_ref_id", postgres.ConstraintName);
    }

    private async Task<List<WalletMutation>> RowsForAsync(Guid invoiceId)
    {
        await using var db = pg.NewDbContext();
        return await db.WalletMutations.AsNoTracking()
            .Where(c => c.RefType == WalletMutationConsumer.InvoiceRefType && c.RefId == invoiceId)
            .ToListAsync();
    }

    // ── The index ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SecondRowForAnInvoice_IsRefused_EvenWhenTheFirstFailed()
    {
        var walletId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        await using (var db = pg.NewDbContext())
        {
            db.WalletMutations.Add(FailedDebit(walletId, invoiceId));
            await db.SaveChangesAsync();
        }

        await using (var db = pg.NewDbContext())
        {
            db.WalletMutations.Add(WalletMutation.ForDebit(walletId, 100_000L, WalletMutationConsumer.InvoiceRefType, invoiceId));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            AssertRefIndexRefused(ex);
        }

        Assert.Single(await RowsForAsync(invoiceId));
    }

    [Fact]
    public async Task LiveInvoiceDebit_HoldsTheRef()
    {
        var walletId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        await using (var db = pg.NewDbContext())
        {
            db.WalletMutations.Add(WalletMutation.ForDebit(walletId, 100_000L, WalletMutationConsumer.InvoiceRefType, invoiceId));
            await db.SaveChangesAsync();
        }

        await using (var db = pg.NewDbContext())
        {
            db.WalletMutations.Add(WalletMutation.ForDebit(walletId, 100_000L, WalletMutationConsumer.InvoiceRefType, invoiceId));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            AssertRefIndexRefused(ex);
        }
    }

    // A top-up is confirmed once; a failed credit of it is not a reason to credit it again.
    [Fact]
    public async Task FailedTopUpCredit_HoldsTheRef()
    {
        var walletId = Guid.NewGuid();
        var topUpId = Guid.NewGuid();
        await using (var db = pg.NewDbContext())
        {
            var failed = WalletMutation.ForCredit(walletId, 100_000L, "TopUp", topUpId);
            failed.MarkFailed("Gave up (test).");
            db.WalletMutations.Add(failed);
            await db.SaveChangesAsync();
        }

        await using (var db = pg.NewDbContext())
        {
            db.WalletMutations.Add(WalletMutation.ForCredit(walletId, 100_000L, "TopUp", topUpId));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            AssertRefIndexRefused(ex);
        }
    }

    // ── The customer's path ───────────────────────────────────────────────

    private static PayInvoiceFromWalletCommandHandler PayHandler(TitanDbContext db, Guid tenantId)
    {
        return new PayInvoiceFromWalletCommandHandler(
            new InvoiceRepository(db), new WalletRepository(db), new WalletMutationRepository(db),
            WalletBus.None(), Callers.TenantMember(tenantId));
    }

    /// A tenant whose debit for the invoice failed and who has since topped up: the wallet covers the
    /// invoice, the invoice is still New, and its only debit row is the failed one.
    private async Task<(Wallet Wallet, Invoice Invoice, Guid FailedRowId)> SeedFailedThenToppedUpAsync(
        Guid tenantId, int month, int attempts = 0)
    {
        var wallet = Wallet.Open(tenantId, Currency);
        wallet.Credit(250_000L);
        var items = new List<InvoiceItem>
        {
            InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "vm-1", 100_000L, Currency)
        };
        var invoice = Invoice.Generate(tenantId, 2026, month, DateTimeOffset.UtcNow.AddDays(-2), items, Currency);
        var failed = FailedDebit(wallet.Id, invoice.Id, attempts: attempts);

        await using var db = pg.NewDbContext();
        db.Wallets.Add(wallet);
        db.Invoices.Add(invoice);
        db.WalletMutations.Add(failed);
        await db.SaveChangesAsync();
        return (wallet, invoice, failed.Id);
    }

    private async Task ApplyAsync(Guid mutationId)
    {
        await using var db = pg.NewDbContext();
        await new WalletMutationConsumer(
                new WalletMutationRepository(db), new WalletRepository(db), new InvoiceRepository(db))
            .ApplyAsync(mutationId);
    }

    private async Task AssertPaidOnceAsync(Guid tenantId, Guid invoiceId)
    {
        await using var db = pg.NewDbContext();
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(w => w.TenantId == tenantId);
        int spends = await db.WalletTransactions.AsNoTracking()
            .CountAsync(t => t.WalletId == wallet.Id && t.RefId == invoiceId);
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);

        Assert.Equal(150_000L, wallet.AvailableAmount);
        Assert.Equal(1, spends);
        Assert.Equal(InvoiceStatus.SettledByWallet, invoice.Status);
    }

    [Fact]
    public async Task PayAgain_AfterAFailedDebit_ReopensTheSameRow_DebitsOnce_AndSettles()
    {
        var tenantId = Guid.NewGuid();
        var (_, invoice, failedRowId) = await SeedFailedThenToppedUpAsync(tenantId, month: 1);

        await using (var db = pg.NewDbContext())
            await PayHandler(db, tenantId).HandleAsync(new PayInvoiceFromWalletCommand(invoice.Id));

        var reopened = Assert.Single(await RowsForAsync(invoice.Id));
        Assert.Equal(failedRowId, reopened.Id);
        Assert.Equal(WalletMutationStatus.Pending, reopened.Status);

        await ApplyAsync(failedRowId);

        await AssertPaidOnceAsync(tenantId, invoice.Id);
        var applied = Assert.Single(await RowsForAsync(invoice.Id));
        Assert.Equal(WalletMutationStatus.Applied, applied.Status);
    }

    // A row the sweeper gave up on starts its attempt count again: otherwise the first transient error after
    // the reopen would give up on it at once.
    [Fact]
    public async Task PayAgain_AfterTheSweeperGaveUp_StartsTheAttemptCountAgain()
    {
        var tenantId = Guid.NewGuid();
        var (_, invoice, failedRowId) = await SeedFailedThenToppedUpAsync(tenantId, month: 5, attempts: 5);

        await using (var db = pg.NewDbContext())
            await PayHandler(db, tenantId).HandleAsync(new PayInvoiceFromWalletCommand(invoice.Id));

        var reopened = Assert.Single(await RowsForAsync(invoice.Id));
        Assert.Equal(failedRowId, reopened.Id);
        Assert.Equal(0, reopened.Attempts);
    }

    // While the reopened debit waits, the invoice reads like any payment in flight: Processing, not Failed,
    // and outside money cannot be recorded on it.
    [Fact]
    public async Task PayAgain_WhileTheReopenedDebitWaits_ReadsProcessing_AndRefusesMarkPaid()
    {
        var tenantId = Guid.NewGuid();
        var (_, invoice, _) = await SeedFailedThenToppedUpAsync(tenantId, month: 3);

        await using (var db = pg.NewDbContext())
            await PayHandler(db, tenantId).HandleAsync(new PayInvoiceFromWalletCommand(invoice.Id));

        await using (var db = pg.NewDbContext())
        {
            var listed = await new ListInvoicesQueryHandler(new InvoiceRepository(db), new WalletMutationRepository(db))
                .HandleAsync(new ListInvoicesQuery(null));
            Assert.Equal("Processing", Assert.Single(listed, i => i.Id == invoice.Id).PaymentState);
        }

        await using (var db = pg.NewDbContext())
        {
            var ex = await Assert.ThrowsAsync<ConflictException>(() =>
                new MarkInvoicePaidCommandHandler(new InvoiceRepository(db), new WalletMutationRepository(db), Callers.Admin("admin-1"))
                    .HandleAsync(new MarkInvoicePaidCommand(invoice.Id)));
            Assert.Equal("INVOICE_PAYMENT_CONFLICT", ex.Code);
        }
    }

    // The admin's settle-from-wallet button follows the same rule as the customer's.
    [Fact]
    public async Task AdminSettle_AfterAFailedDebit_ReopensTheSameRow_AndSettles()
    {
        var tenantId = Guid.NewGuid();
        var (_, invoice, failedRowId) = await SeedFailedThenToppedUpAsync(tenantId, month: 4);

        await using (var db = pg.NewDbContext())
        {
            await new SettleInvoiceFromWalletCommandHandler(
                    new InvoiceRepository(db), new WalletRepository(db), new WalletMutationRepository(db),
                    WalletBus.None(), new AdminAuditLogRepository(db), Callers.Admin("admin-1"))
                .HandleAsync(new SettleInvoiceFromWalletCommand(invoice.Id));
        }

        Assert.Equal(failedRowId, Assert.Single(await RowsForAsync(invoice.Id)).Id);
        await ApplyAsync(failedRowId);
        await AssertPaidOnceAsync(tenantId, invoice.Id);
    }

    // Two presses after the failure, many times over: both answer like a first press, the invoice keeps one
    // row, and the wallet is drawn once.
    [Fact]
    public async Task ConcurrentPayAgain_AfterAFailedDebit_AnswersBoth_KeepsOneRow_DebitsOnce_RepeatedManyTimes()
    {
        for (var i = 0; i < RaceIterations; i++)
        {
            var tenantId = Guid.NewGuid();
            var (_, invoice, failedRowId) = await SeedFailedThenToppedUpAsync(tenantId, month: 2);

            await using var dbA = pg.NewDbContext();
            await using var dbB = pg.NewDbContext();

            async Task<string> Attempt(TitanDbContext db)
            {
                try
                {
                    await PayHandler(db, tenantId).HandleAsync(new PayInvoiceFromWalletCommand(invoice.Id));
                    return "accepted";
                }
                catch (ConflictException ex)
                {
                    return ex.Code;
                }
            }

            var answers = await Task.WhenAll(Attempt(dbA), Attempt(dbB));

            Assert.True(answers.All(a => a == "accepted"), $"iter {i}: answers={string.Join(",", answers)}");
            var rows = await RowsForAsync(invoice.Id);
            Assert.True(rows.Count == 1, $"iter {i}: rows={rows.Count} (expected exactly 1)");
            Assert.Equal(WalletMutationStatus.Pending, rows[0].Status);

            await ApplyAsync(failedRowId);
            await AssertPaidOnceAsync(tenantId, invoice.Id);
        }
    }

    // The race the status token exists for: press B read the row as Failed, press A reopened it and the consumer
    // applied it, then B reopens its stale copy. B's save must fail, or Pending lands over Applied and the
    // consumer runs the payment a second time.
    [Fact]
    public async Task StaleReopen_AfterTheRowWasReopenedAndApplied_IsRefused_AndTheMoneyMovedOnce()
    {
        var tenantId = Guid.NewGuid();
        var (_, invoice, failedRowId) = await SeedFailedThenToppedUpAsync(tenantId, month: 6);

        await using var dbStale = pg.NewDbContext();
        var staleCopy = await new WalletMutationRepository(dbStale)
            .GetForRefForUpdateAsync(WalletMutationConsumer.InvoiceRefType, invoice.Id, default);
        Assert.Equal(WalletMutationStatus.Failed, staleCopy!.Status);

        await using (var db = pg.NewDbContext())
            await PayHandler(db, tenantId).HandleAsync(new PayInvoiceFromWalletCommand(invoice.Id));
        await ApplyAsync(failedRowId);

        staleCopy.ReopenFailedDebit();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbStale.SaveChangesAsync());

        var row = Assert.Single(await RowsForAsync(invoice.Id));
        Assert.Equal(WalletMutationStatus.Applied, row.Status);
        await AssertPaidOnceAsync(tenantId, invoice.Id);
    }

    // A reopened row keeps the created_at of its first press. The sweeper must still treat it as fresh, or it
    // takes the row while the bus notify of the reopen is in flight: two writers on one wallet.
    [Fact]
    public async Task ReopenedRow_FirstPressedLongAgo_IsNotSweptBeforeItsStaleWindow()
    {
        var tenantId = Guid.NewGuid();
        var (_, invoice, failedRowId) = await SeedFailedThenToppedUpAsync(tenantId, month: 7);
        var longAgo = DateTimeOffset.UtcNow.AddDays(-2);
        await using (var db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.wallet_mutations SET created_at = {longAgo}, queued_at = {longAgo} WHERE id = {failedRowId}");
        }

        await using (var db = pg.NewDbContext())
            await PayHandler(db, tenantId).HandleAsync(new PayInvoiceFromWalletCommand(invoice.Id));

        await using (var db = pg.NewDbContext())
        {
            var stale = await new WalletMutationRepository(db)
                .ListPendingOlderThanAsync(DateTimeOffset.UtcNow.AddSeconds(-60), 100, default);
            Assert.DoesNotContain(stale, c => c.Id == failedRowId);
        }

        var row = Assert.Single(await RowsForAsync(invoice.Id));
        Assert.Equal(WalletMutationStatus.Pending, row.Status);
        Assert.Equal(longAgo.ToUnixTimeMilliseconds(), row.CreatedAt.ToUnixTimeMilliseconds());
    }

    // During a rollout the previous image still inserts rows without naming queued_at; the column default keeps
    // those inserts working instead of failing a top-up confirm or a first payment with a not-null violation.
    [Fact]
    public async Task InsertThatDoesNotNameQueuedAt_StillLands_QueuedNow()
    {
        var id = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await using var db = pg.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO core.wallet_mutations
              (id, wallet_id, type, amount, ref_type, ref_id, status, created_at, attempts)
            VALUES ({id}, {Guid.NewGuid()}, 'Credit', 1000, 'TopUp', {Guid.NewGuid()}, 'Pending', now(), 0)
            """);

        var row = await db.WalletMutations.AsNoTracking().SingleAsync(c => c.Id == id);
        Assert.True(row.QueuedAt >= before, $"queued_at {row.QueuedAt:O} is not the insert time");
    }
}

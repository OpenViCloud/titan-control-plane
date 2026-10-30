using Titan.Domain.Values;
using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Application.Queries.Billing.Invoice;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.WalletMutation;

namespace Titan.Tests.Infrastructure.Integration;

/// <summary>
/// titan-ops architecture/money-and-wallet.md#human-confirmed-debit — paying an outstanding invoice from the wallet
/// through the settle handler (the admin button): the request enqueues one debit mutation, and the
/// single-writer consumer debits the Wallet aggregate and flips the invoice in one SaveChanges. What only
/// real Postgres can prove:
/// <list type="bullet">
///   <item>the mutation's UNIQUE(ref_type, ref_id) is the serialization point, so two concurrent
///         settlements of one invoice cannot both draw the wallet down — a double-click, or the tenant
///         and an admin pressing pay at once, pays exactly once;</item>
///   <item>the debit and the status flip share one SaveChanges (one transaction) — on this path money is
///         spent iff the invoice is settled, and a wallet can never go negative (Wallet.Debit). An admin
///         MarkPaid that lands between enqueue and apply fails the debit instead of charging twice;</item>
///   <item>balance and ledger stay equal: every committed debit leaves exactly one matching movement;</item>
///   <item>`ListOutstandingByTenantAsync` returns Status == New rows including long-overdue ones —
///         `Overdue` is derived, never stored (titan-ops architecture/money-and-wallet.md#invoice-frozen-snapshot).</item>
/// </list>
/// Category=Integration — its own CI step (InMemory enforces neither the transaction nor the unique
/// index).
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class DebtSettlementDbTests(PostgresFixture pg)
{
    private const string Currency = "VND";

    // The admin settle handler over real repositories. The admin path is not tenant-scoped; the caller is
    // read only as the audit actor.
    private static AdminInvoices NewInvoiceService(TitanDbContext db) => new(db, WalletBus.None());

    private sealed class AdminInvoices(TitanDbContext db, IWalletMutationPublisher bus)
    {
        private static ICurrentUser Admin()
        {
            return Callers.Admin("admin-1");
        }

        public async Task<InvoiceQueryResult> SettleFromWalletAsAdminAsync(Guid invoiceId, CancellationToken ct)
        {
            await new SettleInvoiceFromWalletCommandHandler(
                    new InvoiceRepository(db), new WalletRepository(db), new WalletMutationRepository(db), bus,
                    new AdminAuditLogRepository(db), Admin())
                .HandleAsync(new SettleInvoiceFromWalletCommand(invoiceId), ct);
            // titan-ops architecture/http-contract.md#commands-return-no-data: the command returns nothing; read the invoice back the way the admin list does.
            return Assert.Single(await ListAllAsync(null, ct), i => i.Id == invoiceId);
        }

        public Task<IReadOnlyList<InvoiceQueryResult>> ListAllAsync(string? status, CancellationToken ct)
            => new ListInvoicesQueryHandler(new InvoiceRepository(db), new WalletMutationRepository(db))
                .HandleAsync(new ListInvoicesQuery(status), ct);
    }

    private static Invoice NewOutstandingInvoice(Guid tenantId, int month, long amount, DateTimeOffset issuedAt)
    {
        var items = new List<InvoiceItem>
        {
            InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "web-01", amount, Currency),
        };
        return Invoice.Generate(tenantId, 2026, month, issuedAt, items, Currency);
    }

    private async Task<(InvoiceStatus Status, DateTimeOffset? PaidAt, string? PaidBy)> ReadInvoiceAsync(Guid id)
    {
        await using var db = pg.NewDbContext();
        return await db.Invoices.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new ValueTuple<InvoiceStatus, DateTimeOffset?, string?>(i.Status, i.PaidAt, i.PaidBy))
            .SingleAsync();
    }

    private async Task<long> ReadBalanceAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        var balance = await db.Wallets.AsNoTracking()
            .Where(w => w.TenantId == tenantId).Select(w => w.AvailableAmount).SingleAsync();
        return balance;
    }

    /// The reconciliation invariant, read straight from Postgres: a wallet's balance must equal the sum
    /// of its movements. A stored balance can drift, so every test that moves money asserts this too.
    private async Task AssertReconciledAsync(Guid tenantId, long expected)
    {
        await using var db = pg.NewDbContext();
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(w => w.TenantId == tenantId);
        var movements = await db.WalletTransactions.AsNoTracking()
            .Where(t => t.WalletId == wallet.Id).ToListAsync();

        Assert.NotEmpty(movements);                       // non-empty guard: 0 == 0 proves nothing
        Assert.Equal(expected, wallet.AvailableAmount);
        Assert.Equal(wallet.AvailableAmount, movements.Sum(m => m.Amount));
    }

    /// Seeds a wallet holding `amount`, with the single inflow movement that put it there — the state a
    /// confirmed top-up leaves behind. Balance set via SQL (fixture setup, not the code under test);
    /// version stays at its default 0.
    private async Task SeedWalletAsync(Guid tenantId, long amount)
    {
        var wallet = Wallet.Open(tenantId, Currency);
        await using var db = pg.NewDbContext();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE core.wallets SET available_amount = {amount} WHERE id = {wallet.Id}");
        db.WalletTransactions.Add(
            WalletTransaction.ForTopUp(wallet.Id, amount, Guid.NewGuid(), amount));
        await db.SaveChangesAsync();
    }

    private async Task SeedInvoiceAsync(Invoice invoice)
    {
        await using var db = pg.NewDbContext();
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
    }

    /// The debit mutations raised against one invoice — the "exactly one debit" oracle.
    private async Task<List<Guid>> MutationsForInvoiceAsync(Guid invoiceId)
    {
        await using var db = pg.NewDbContext();
        return await db.WalletMutations.AsNoTracking()
            .Where(c => c.RefType == "Invoice" && c.RefId == invoiceId)
            .Select(c => c.Id)
            .ToListAsync();
    }

    /// titan-ops architecture/money-and-wallet.md#single-writer-outbox — runs the debit mutation(s) an invoice payment
    /// queued, the way the subscriber or the sweeper would. Applying them by hand keeps these tests deterministic: they are about what the
    /// settlement DOES, not about how fast a background service gets to it.
    private async Task DrainMutationsAsync(Guid invoiceId)
    {
        List<Guid> mutationIds;
        await using (var db = pg.NewDbContext())
        {
            mutationIds = await db.WalletMutations.AsNoTracking()
                .Where(c => c.RefType == "Invoice" && c.RefId == invoiceId)
                .OrderBy(c => c.CreatedAt)
                .Select(c => c.Id)
                .ToListAsync();
        }

        foreach (var id in mutationIds)
        {
            await using var db = pg.NewDbContext();
            await new WalletMutationConsumer(
                new WalletMutationRepository(db),
                new WalletRepository(db),
                new InvoiceRepository(db)).ApplyAsync(id);
        }
    }

    [Fact]
    public async Task Settle_DrawsWalletDown_AndFlipsInvoiceToSettledByWallet()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 500L);
        var invoice = NewOutstandingInvoice(tenantId, 5, 200L, DateTimeOffset.UtcNow.AddDays(-10));
        await SeedInvoiceAsync(invoice);

        // titan-ops architecture/money-and-wallet.md#single-writer-outbox — pressing pay ACCEPTS the payment; the
        // money moves in the consumer, so the wallet and invoice only change after the drain.
        await using (var db = pg.NewDbContext())
        {
            var dto = await NewInvoiceService(db).SettleFromWalletAsAdminAsync(invoice.Id, default);
            Assert.Equal("Processing", dto.PaymentState);
            // Still outstanding at this instant — nothing has been charged.
            Assert.Equal(InvoiceDisplayStatus.Overdue, dto.Status);
            Assert.Equal(500L, await ReadBalanceAsync(tenantId));
        }

        await DrainMutationsAsync(invoice.Id);

        var (status, storedPaidAt, paidBy) = await ReadInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.SettledByWallet, status);
        Assert.Equal("wallet", paidBy);
        Assert.NotNull(storedPaidAt);
        Assert.Equal(300L, await ReadBalanceAsync(tenantId));
        await AssertReconciledAsync(tenantId, 300L);
    }

    // MarkPaid refuses while the debit is Pending, but one that passed that check just before the enqueue still
    // lands between enqueue and apply. Saved through the aggregate here, as that MarkPaid would.
    [Fact]
    public async Task MarkPaidBetweenEnqueueAndApply_FailsTheDebit_AndLeavesTheWallet()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 500L);
        var invoice = NewOutstandingInvoice(tenantId, 6, 200L, DateTimeOffset.UtcNow.AddDays(-10));
        await SeedInvoiceAsync(invoice);
        await using (var db = pg.NewDbContext())
        {
            await NewInvoiceService(db).SettleFromWalletAsAdminAsync(invoice.Id, default);
        }
        await using (var db = pg.NewDbContext())
        {
            Invoice stored = await db.Invoices.SingleAsync(i => i.Id == invoice.Id);
            stored.MarkPaid(DateTimeOffset.UtcNow, "admin-2");
            await db.SaveChangesAsync();
        }

        await DrainMutationsAsync(invoice.Id);

        await using var verify = pg.NewDbContext();
        WalletMutation debit = await verify.WalletMutations.AsNoTracking()
            .SingleAsync(c => c.RefType == "Invoice" && c.RefId == invoice.Id);
        Assert.Equal(WalletMutationStatus.Failed, debit.Status);
        var (status, _, paidBy) = await ReadInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.Paid, status);
        Assert.Equal("admin-2", paidBy);
        await AssertReconciledAsync(tenantId, 500L);
    }

    [Fact]
    public async Task Settle_InvoiceAlreadySettled_IsARepeat_AndLeavesWalletUntouched()
    {
        // The retried-webhook / double-click case. The wallet already paid this invoice, so a second
        // press is a repeat: it answers like the first and never reaches the wallet.
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 500L);
        var invoice = NewOutstandingInvoice(tenantId, 6, 200L, DateTimeOffset.UtcNow.AddDays(-10));
        invoice.SettleFromWallet(DateTimeOffset.UtcNow);   // seeded as ALREADY settled
        await SeedInvoiceAsync(invoice);

        await using var db = pg.NewDbContext();
        await NewInvoiceService(db).SettleFromWalletAsAdminAsync(invoice.Id, default);

        Assert.Equal(500L, await ReadBalanceAsync(tenantId));     // wallet untouched
        await AssertReconciledAsync(tenantId, 500L);              // ...and no orphan ledger row
    }

    [Fact]
    public async Task Settle_WalletCannotCover_Throws_AndDoesNotSettleTheInvoice()
    {
        // If the money is not there the invoice stays outstanding and the balance stays put.
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 50L);
        var invoice = NewOutstandingInvoice(tenantId, 7, 200L, DateTimeOffset.UtcNow.AddDays(-10));
        await SeedInvoiceAsync(invoice);

        await using var db = pg.NewDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewInvoiceService(db).SettleFromWalletAsAdminAsync(invoice.Id, default));

        Assert.Equal("INVOICE_WALLET_INSUFFICIENT", ex.Code);
        var (status, _, _) = await ReadInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.New, status);
        Assert.Equal(50L, await ReadBalanceAsync(tenantId));
        await AssertReconciledAsync(tenantId, 50L);
    }

    [Fact]
    public async Task ConcurrentSettle_SameInvoice_OnlyOneWins_AndTheWalletIsDrawnDownOnce()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 1000L);
        var invoice = NewOutstandingInvoice(tenantId, 8, 200L, DateTimeOffset.UtcNow.AddDays(-10));
        await SeedInvoiceAsync(invoice);

        // Two independent contexts, same invoice — a double-click, or the tenant and an admin pressing
        // pay at the same moment.
        await using var dbA = pg.NewDbContext();
        await using var dbB = pg.NewDbContext();

        // Both presses are the same intent, so both answer success — whatever the timing:
        //   • they serialise → the second sees the first's waiting debit and is recognised as a repeat;
        //   • they overlap   → the second collides at UNIQUE(ref_type, ref_id), finds the first's debit,
        //                      and answers like it.
        // What must never happen is a second debit, asserted where the money is: one mutation, and the
        // wallet drawn once (below).
        async Task<bool> Attempt(TitanDbContext db)
        {
            try { await NewInvoiceService(db).SettleFromWalletAsAdminAsync(invoice.Id, default); return true; }
            catch (ConflictException ex) when (ex.Code == "INVOICE_PAYMENT_CONFLICT") { return false; }
        }

        var results = await Task.WhenAll(Attempt(dbA), Attempt(dbB));

        Assert.All(results, answered => Assert.True(answered));    // a repeat is not an error
        Assert.Single(await MutationsForInvoiceAsync(invoice.Id));  // exactly one debit
        // The losing press staged its own audit row too; its save rolled back, so only the winner's remains.
        await using (var readDb = pg.NewDbContext())
        {
            int auditRows = await readDb.AdminAuditLogs.AsNoTracking()
                .CountAsync(a => a.Action == "invoice.settle_from_wallet" && a.ResourceId == invoice.Id);
            Assert.Equal(1, auditRows);
        }

        // The serialization point is UNIQUE(ref_type, ref_id) on the mutation, reached before any money is
        // involved: exactly one debit exists, and draining it charges the wallet once.
        await DrainMutationsAsync(invoice.Id);

        Assert.Equal(800L, await ReadBalanceAsync(tenantId));       // charged once, not twice
        await AssertReconciledAsync(tenantId, 800L);                // ...and exactly one spend recorded
        var (status, _, _) = await ReadInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.SettledByWallet, status);
    }

    [Fact]
    public async Task ListOutstanding_ReturnsNewInvoices_IncludingLongOverdueOnes_OldestFirst()
    {
        // The "Overdue is not a stored status" trap, pinned against real SQL: a 40-day-old New invoice
        // IS outstanding. A query filtering on the string "Overdue" returns nothing here.
        var tenantId = Guid.NewGuid();
        var veryOld = NewOutstandingInvoice(tenantId, 9, 100L, DateTimeOffset.UtcNow.AddDays(-40));
        var recent = NewOutstandingInvoice(tenantId, 10, 100L, DateTimeOffset.UtcNow.AddDays(-1));
        var settled = NewOutstandingInvoice(tenantId, 11, 100L, DateTimeOffset.UtcNow.AddDays(-20));
        settled.SettleFromWallet(DateTimeOffset.UtcNow);

        await using (var seed = pg.NewDbContext())
        {
            seed.Invoices.AddRange(veryOld, recent, settled);
            await seed.SaveChangesAsync();
        }

        await using var db = pg.NewDbContext();
        var outstanding = await new InvoiceRepository(db).ListOutstandingByTenantAsync(tenantId, default);

        Assert.Equal(2, outstanding.Count);
        Assert.Equal(veryOld.Id, outstanding[0].Id);      // oldest first — FIFO does not depend on the caller
        Assert.Equal(recent.Id, outstanding[1].Id);
        Assert.DoesNotContain(settled.Id, outstanding.Select(i => i.Id));
    }
}

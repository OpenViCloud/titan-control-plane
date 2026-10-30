using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Application.Queries.Billing.Invoice;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Application.Services.Billing.WalletMutations;

namespace Titan.Tests.Infrastructure.Integration;

/// titan-ops architecture/money-and-wallet.md#single-writer-outbox — paying an invoice from the wallet, asynchronously, on real Postgres.
///
/// What only Postgres can prove here, and why unit tests are not the gate:
///  - a second press raises no second debit (the waiting-debit read catches it; two presses at the same
///    instant are DebtSettlementDbTests' case, behind the UNIQUE(ref_type, ref_id) index);
///  - that the request commits a mutation WITHOUT touching the balance — a real transaction
///    is the only thing that can be read back and checked;
///  - the negative case the whole design turns on: a payment that passed its pre-check and
///    then could not be honoured leaves the wallet NON-NEGATIVE and the invoice unpaid.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class InvoicePaymentAsyncDbTests(PostgresFixture pg)
{
    private const string Currency = "VND";

    // The admin settle handler + admin list over real repositories.
    private static AdminInvoices NewService(TitanDbContext db, IWalletMutationPublisher bus) => new(db, bus);

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

    private static WalletMutationConsumer NewConsumer(TitanDbContext db) =>
        new(new WalletMutationRepository(db), new WalletRepository(db), new InvoiceRepository(db));

    private async Task<Wallet> SeedWalletAsync(Guid tenantId, long balance)
    {
        var wallet = Wallet.Open(tenantId, Currency);
        if (balance > 0) wallet.Credit(balance);

        await using var db = pg.NewDbContext();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        return wallet;
    }

    private async Task<Invoice> SeedInvoiceAsync(Guid tenantId, long total, int month)
    {
        var items = new List<InvoiceItem>
        {
            InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "vm-1", total, Currency)
        };
        var invoice = Invoice.Generate(tenantId, 2026, month, DateTimeOffset.UtcNow.AddDays(-2), items, Currency);

        await using var db = pg.NewDbContext();
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    private async Task<(long Balance, int Movements)> WalletStateAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        var wallet = await db.Wallets.AsNoTracking().SingleAsync(w => w.TenantId == tenantId);
        var movements = await db.WalletTransactions.AsNoTracking().CountAsync(t => t.WalletId == wallet.Id);
        return (wallet.AvailableAmount, movements);
    }

    private async Task<Invoice> ReadInvoiceAsync(Guid invoiceId)
    {
        await using var db = pg.NewDbContext();
        return await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
    }

    private async Task<List<WalletMutation>> MutationsForAsync(Guid invoiceId)
    {
        await using var db = pg.NewDbContext();
        return await db.WalletMutations.AsNoTracking()
            .Where(c => c.RefType == "Invoice" && c.RefId == invoiceId)
            .ToListAsync();
    }

    private async Task<List<AdminAuditLog>> AuditRowsForAsync(Guid invoiceId)
    {
        await using var db = pg.NewDbContext();
        return await db.AdminAuditLogs.AsNoTracking()
            .Where(a => a.ResourceType == "invoice" && a.ResourceId == invoiceId)
            .ToListAsync();
    }

    // The request writes a mutation and moves no money.
    [Fact]
    public async Task Pay_EnqueuesDebitMutation_DoesNotDebitYet()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 500_000L);
        var invoice = await SeedInvoiceAsync(tenantId, 300_000L, month: 1);
        var bus = WalletBus.None();

        await using (var db = pg.NewDbContext())
            await NewService(db, bus).SettleFromWalletAsAdminAsync(invoice.Id, default);

        var mutation = Assert.Single(await MutationsForAsync(invoice.Id));
        Assert.Equal(WalletMutationType.Debit, mutation.Type);
        Assert.Equal(300_000L, mutation.Amount);
        Assert.Equal(WalletMutationStatus.Pending, mutation.Status);

        // Committed, and nothing else was: the balance is untouched, no ledger row exists, and the
        // invoice is still outstanding. An invoice that read "paid" here would be paid with no money.
        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(500_000L, balance);
        Assert.Equal(0, movements);
        Assert.Equal(InvoiceStatus.New, (await ReadInvoiceAsync(invoice.Id)).Status);
        // The audit row commits with the debit request, in the same SaveChanges.
        AdminAuditLog audit = Assert.Single(await AuditRowsForAsync(invoice.Id));
        Assert.Equal("invoice.settle_from_wallet", audit.Action);
        Assert.Equal(tenantId, audit.OwnerTenantId);
    }

    // The consumer then debits, writes ONE ledger row and settles the invoice, together.
    [Fact]
    public async Task Pay_ThenConsumerApplies_DebitsOnce_SettlesInvoice()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 500_000L);
        var invoice = await SeedInvoiceAsync(tenantId, 300_000L, month: 2);
        var bus = WalletBus.None();

        await using (var db = pg.NewDbContext())
            await NewService(db, bus).SettleFromWalletAsAdminAsync(invoice.Id, default);

        var mutationId = (await MutationsForAsync(invoice.Id)).Single().Id;
        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync(mutationId);

        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(200_000L, balance);
        Assert.Equal(1, movements);

        var settled = await ReadInvoiceAsync(invoice.Id);
        Assert.Equal(InvoiceStatus.SettledByWallet, settled.Status);
        Assert.Equal("wallet", settled.PaidBy);
        Assert.NotNull(settled.PaidAt);
    }

    // A wallet that cannot cover the invoice is refused AT THE BUTTON, and the refusal must
    // leave no mutation behind. This is the negative that proves the synchronous pre-check still exists:
    // delete it and this test is the one that goes red, not a comment.
    [Fact]
    public async Task Pay_InsufficientBalance_Refuses_AndEnqueuesNothing()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 299_999L);
        var invoice = await SeedInvoiceAsync(tenantId, 300_000L, month: 3);

        await using (var db = pg.NewDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                NewService(db, WalletBus.None())
                    .SettleFromWalletAsAdminAsync(invoice.Id, default));
            Assert.Equal("INVOICE_WALLET_INSUFFICIENT", ex.Code);
        }

        Assert.Empty(await MutationsForAsync(invoice.Id));
        Assert.Equal(299_999L, (await WalletStateAsync(tenantId)).Balance);
        // The audit row is staged before the refusal; it must not commit on its own.
        Assert.Empty(await AuditRowsForAsync(invoice.Id));
    }

    // Pressing pay twice raises ONE debit. The second press is recognised as a repeat by the waiting-debit read;
    // the unique index stands behind it for two presses at the same instant.
    [Fact]
    public async Task Pay_Twice_EnqueuesExactlyOneMutation()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 900_000L);
        var invoice = await SeedInvoiceAsync(tenantId, 300_000L, month: 4);

        await using (var db = pg.NewDbContext())
            await NewService(db, WalletBus.None())
                .SettleFromWalletAsAdminAsync(invoice.Id, default);

        // The second press: the invoice is still New (the money has not moved), but its debit is
        // waiting — so it is a repeat and answers like the first, enqueuing nothing. The unique index
        // stands behind the check for two presses at the same instant (DebtSettlementDbTests.ConcurrentSettle_SameInvoice_OnlyOneWins_AndTheWalletIsDrawnDownOnce).
        await using (var db = pg.NewDbContext())
            await NewService(db, WalletBus.None())
                .SettleFromWalletAsAdminAsync(invoice.Id, default);

        Assert.Single(await MutationsForAsync(invoice.Id));

        // And after the consumer runs, the customer paid once.
        var mutationId = (await MutationsForAsync(invoice.Id)).Single().Id;
        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync(mutationId);

        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(600_000L, balance);
        Assert.Equal(1, movements);
    }

    // ⭐ THE case titan-ops architecture/money-and-wallet.md#single-writer-outbox exists for, and the reason the
    // pre-check is not a guard.
    //
    // No threads and no clock: two invoices are each paid while the wallet still shows the full balance,
    // because NEITHER debit has been applied yet. The first apply drains the wallet; the second then
    // cannot be honoured. What must hold is that the shortfall costs the customer nothing — the balance
    // does not go negative and the unpayable invoice stays unpaid.
    [Fact]
    public async Task SecondDebit_WalletDrainedByFirst_Fails_LeavesBalanceNonNegative()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 100_000L);
        var first = await SeedInvoiceAsync(tenantId, 100_000L, month: 5);
        var second = await SeedInvoiceAsync(tenantId, 100_000L, month: 6);

        await using (var db = pg.NewDbContext())
            await NewService(db, WalletBus.None())
                .SettleFromWalletAsAdminAsync(first.Id, default);

        // Precondition asserted right before the act: the wallet still reads the full balance, which is
        // exactly why the second pre-check passes.
        Assert.Equal(100_000L, (await WalletStateAsync(tenantId)).Balance);

        await using (var db = pg.NewDbContext())
            await NewService(db, WalletBus.None())
                .SettleFromWalletAsAdminAsync(second.Id, default);

        var firstMutation = (await MutationsForAsync(first.Id)).Single();
        var secondMutation = (await MutationsForAsync(second.Id)).Single();

        await using (var db = pg.NewDbContext()) await NewConsumer(db).ApplyAsync(firstMutation.Id);
        await using (var db = pg.NewDbContext()) await NewConsumer(db).ApplyAsync(secondMutation.Id);

        var (balance, movements) = await WalletStateAsync(tenantId);
        Assert.Equal(0L, balance);          // NON-NEGATIVE — the invariant this test protects
        Assert.Equal(1, movements);         // one debit happened, not two

        await using (var db = pg.NewDbContext())
        {
            var reread = await db.WalletMutations.AsNoTracking()
                .SingleAsync(c => c.Id == secondMutation.Id);
            Assert.Equal(WalletMutationStatus.Failed, reread.Status);
            Assert.Contains("100000", reread.LastError!);
        }

        Assert.Equal(InvoiceStatus.SettledByWallet, (await ReadInvoiceAsync(first.Id)).Status);
        Assert.Equal(InvoiceStatus.New, (await ReadInvoiceAsync(second.Id)).Status);
    }

    // The customer can see which is which, without the word "mutation" appearing anywhere.
    [Fact]
    public async Task Invoice_ReportsProcessingThenSettled()
    {
        var tenantId = Guid.NewGuid();
        await SeedWalletAsync(tenantId, 500_000L);
        var invoice = await SeedInvoiceAsync(tenantId, 300_000L, month: 7);
        var bus = WalletBus.None();

        await using (var db = pg.NewDbContext())
            await NewService(db, bus).SettleFromWalletAsAdminAsync(invoice.Id, default);

        await using (var db = pg.NewDbContext())
        {
            var dtos = await NewService(db, bus).ListAllAsync(null, default);
            Assert.Equal("Processing", dtos.Single(d => d.Id == invoice.Id).PaymentState);
        }

        await using (var db = pg.NewDbContext())
            await NewConsumer(db).ApplyAsync((await MutationsForAsync(invoice.Id)).Single().Id);

        await using (var db = pg.NewDbContext())
        {
            var dtos = await NewService(db, bus).ListAllAsync(null, default);
            Assert.Equal("Settled", dtos.Single(d => d.Id == invoice.Id).PaymentState);
        }
    }
}

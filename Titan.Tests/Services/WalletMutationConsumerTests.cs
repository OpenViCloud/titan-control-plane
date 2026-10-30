using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the single-writer consumer's branches,
// against stub repositories so they run without Docker.
//
// Silent about atomicity (one SaveChanges = one transaction), the ledger UNIQUE(type, ref_id) that
// InMemory does not enforce, and the ordering a real wallet threads through BalanceAfter — that is
// WalletMutationConsumerDbTests.
public class WalletMutationConsumerTests
{
    // Returns whatever mutation it was given (or null) — the consumer only reads it by id.
    private sealed class StubMutationRepo(WalletMutation? mutation) : IWalletMutationRepository
    {
        // Not exercised by consumer tests; the health snapshot has its own suite.
        public Task<WalletMutationBacklog> GetHealthSnapshotAsync(
            DateTimeOffset now, int maxAttempts, CancellationToken ct) =>
            Task.FromResult(new WalletMutationBacklog(0, 0, null));

        public Task<WalletMutation?> GetForUpdateAsync(Guid mutationId, CancellationToken ct) =>
            Task.FromResult(mutation);

        // The consumer never enqueues.
        public void Add(WalletMutation mutation) => throw new NotSupportedException();

        // The sweeper's drain and the invoice payment-state lookups, not the consumer's.
        public Task<IReadOnlyList<WalletMutation>> ListPendingOlderThanAsync(
            DateTimeOffset cutoff, int batchSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<WalletMutation?> GetForRefAsync(string refType, Guid refId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<WalletMutation?> GetForRefForUpdateAsync(string refType, Guid refId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, WalletMutation>> GetForRefsAsync(
            string refType, IReadOnlyCollection<Guid> refIds, CancellationToken ct) => throw new NotSupportedException();
    }

    // The consumer settles the invoice a debit was raised for, in the same transaction as the debit.
    // These tests care about WHETHER it did, so the stub just holds one invoice and lets the real
    // aggregate transition run.
    private sealed class StubInvoiceRepo(Titan.Domain.Aggregates.Invoice.Invoice? invoice)
        : IInvoiceRepository
    {
        public Task<Titan.Domain.Aggregates.Invoice.Invoice?> GetForUpdateAsync(Guid id, CancellationToken ct) =>
            Task.FromResult(invoice);

        public Task<bool> TryAddAsync(Titan.Domain.Aggregates.Invoice.Invoice i, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Titan.Domain.Aggregates.Invoice.Invoice?> GetByIdAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Titan.Domain.Aggregates.Invoice.Invoice>> ListByTenantAsync(
            Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Titan.Domain.Aggregates.Invoice.Invoice>> ListOutstandingByTenantAsync(
            Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Titan.Domain.Aggregates.Invoice.Invoice>> ListAllAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<bool> ExistsForPeriodAsync(Guid tenantId, int year, int month, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Titan.Domain.Aggregates.Invoice.Invoice>> ListOutstandingAllAsync(
            CancellationToken ct) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    // Hands back one real Wallet aggregate (so Credit/Debit run for real) and records what the consumer
    // stages, so a test can assert on the ledger movement and whether anything was committed at all.
    private sealed class RecordingWalletRepo(Wallet wallet) : IWalletRepository
    {
        public List<WalletTransaction> Staged { get; } = [];
        public int SaveCount { get; private set; }

        public Task<Wallet?> GetByIdForUpdateAsync(Guid walletId, CancellationToken ct) =>
            Task.FromResult<Wallet?>(wallet);
        public void AddTransaction(WalletTransaction transaction) => Staged.Add(transaction);
        public Task SaveChangesAsync(CancellationToken ct) { SaveCount++; return Task.CompletedTask; }

        // Not on the consumer's path.
        public Task<Wallet?> GetByTenantForUpdateAsync(Guid tenantId, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<long> GetBalanceAsync(Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
        public void Add(Wallet w) => throw new NotSupportedException();
        public Task<IReadOnlyList<WalletTransaction>> ListTransactionsByTenantAsync(Guid tenantId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static Wallet OpenWallet() => Wallet.Open(Guid.NewGuid(), "VND");

    [Fact] // a Pending credit applies exactly once and commits
    public async Task Apply_PendingCredit_CreditsWallet_StagesTopUpMovement_MarksApplied_SavesOnce()
    {
        var wallet = OpenWallet();
        var topUpId = Guid.NewGuid();
        var mutation = WalletMutation.ForCredit(wallet.Id, 100_000L, "TopUp", topUpId);
        var walletRepo = new RecordingWalletRepo(wallet);
        var consumer = new WalletMutationConsumer(new StubMutationRepo(mutation), walletRepo, new StubInvoiceRepo(null));

        await consumer.ApplyAsync(mutation.Id);

        Assert.Equal(100_000L, wallet.AvailableAmount);
        Assert.Equal(WalletMutationStatus.Applied, mutation.Status);
        var movement = Assert.Single(walletRepo.Staged);
        Assert.Equal(WalletTransactionType.TopUp, movement.Type);
        Assert.Equal(100_000L, movement.Amount);          // positive — money in
        Assert.Equal(topUpId, movement.RefId);
        Assert.Equal(1, walletRepo.SaveCount);            // committed once
    }

    [Fact] // a redelivered (already-applied) mutation is a no-op: no credit, no ledger, no commit
    public async Task Apply_AlreadyAppliedMutation_IsNoOp_NoCreditNoLedgerNoSave()
    {
        var wallet = OpenWallet();
        var mutation = WalletMutation.ForCredit(wallet.Id, 100_000L, "TopUp", Guid.NewGuid());
        mutation.MarkApplied();                            // a prior delivery already applied it
        var walletRepo = new RecordingWalletRepo(wallet);
        var consumer = new WalletMutationConsumer(new StubMutationRepo(mutation), walletRepo, new StubInvoiceRepo(null));

        await consumer.ApplyAsync(mutation.Id);

        Assert.Equal(0L, wallet.AvailableAmount);         // balance untouched the second time
        Assert.Empty(walletRepo.Staged);                  // no second ledger row
        Assert.Equal(0, walletRepo.SaveCount);            // nothing committed
    }

    [Fact] // a debit moves money out and records the signed InvoicePayment row
    public async Task Apply_PendingDebit_DebitsWallet_StagesInvoicePayment_MarksApplied()
    {
        var wallet = OpenWallet();
        wallet.Credit(50_000L);                           // fund it so the debit fits
        var invoice = TestInvoice(20_000L);
        var invoiceId = invoice.Id;
        var mutation = WalletMutation.ForDebit(wallet.Id, 20_000L, "Invoice", invoiceId);
        var walletRepo = new RecordingWalletRepo(wallet);
        var consumer = new WalletMutationConsumer(new StubMutationRepo(mutation), walletRepo, new StubInvoiceRepo(invoice));

        await consumer.ApplyAsync(mutation.Id);

        Assert.Equal(30_000L, wallet.AvailableAmount);    // 50k − 20k
        Assert.Equal(WalletMutationStatus.Applied, mutation.Status);
        var movement = Assert.Single(walletRepo.Staged);
        Assert.Equal(WalletTransactionType.InvoicePayment, movement.Type);
        Assert.Equal(-20_000L, movement.Amount);          // signed negative — money out
        Assert.Equal(invoiceId, movement.RefId);
    }

    [Fact] // a notify for a mutation that never landed does nothing
    public async Task Apply_UnknownMutationId_IsNoOp()
    {
        var walletRepo = new RecordingWalletRepo(OpenWallet());
        var consumer = new WalletMutationConsumer(new StubMutationRepo(null), walletRepo, new StubInvoiceRepo(null));

        await consumer.ApplyAsync(Guid.NewGuid());

        Assert.Empty(walletRepo.Staged);
        Assert.Equal(0, walletRepo.SaveCount);
    }

    // ---- The authoritative debit re-check (titan-ops architecture/money-and-wallet.md#single-writer-outbox) ----

    [Fact] // the balance was spent between the button and the apply
    public async Task Apply_DebitAWalletCannotCover_FailsTheMutation_LeavesBalanceUntouched()
    {
        // The pre-check at the button read a balance nothing had been debited from yet. By the time
        // this mutation is applied, an earlier one has drained the wallet. This is the ONLY place that
        // can be caught, which is why the check lives here and not at the request.
        var wallet = OpenWallet();
        wallet.Credit(40_000L);
        var invoiceId = Guid.NewGuid();
        var mutation = WalletMutation.ForDebit(wallet.Id, 100_000L, "Invoice", invoiceId);
        var walletRepo = new RecordingWalletRepo(wallet);
        var invoice = TestInvoice(100_000L);
        var consumer = new WalletMutationConsumer(
            new StubMutationRepo(mutation), walletRepo, new StubInvoiceRepo(invoice));

        await consumer.ApplyAsync(mutation.Id);

        // The mutation is dead, and it says why.
        Assert.Equal(WalletMutationStatus.Failed, mutation.Status);
        Assert.Contains("40000", mutation.LastError);

        // Nothing moved: the balance is what it was, no ledger row, and — the one that matters most —
        // the invoice is NOT settled. An invoice marked paid on a debit that failed is money given away.
        Assert.Equal(40_000L, wallet.AvailableAmount);
        Assert.Empty(walletRepo.Staged);
        Assert.Equal(InvoiceStatus.New, invoice.Status);

        // The verdict is still committed — otherwise the mutation stays Pending and the sweeper retries
        // a debit that can never succeed, forever.
        Assert.Equal(1, walletRepo.SaveCount);
    }

    [Fact] // a debit that fits settles its invoice in the SAME transaction
    public async Task Apply_DebitAWalletCanCover_DebitsSettlesInvoice_AndCommitsOnce()
    {
        var wallet = OpenWallet();
        wallet.Credit(500_000L);
        var mutation = WalletMutation.ForDebit(wallet.Id, 300_000L, "Invoice", Guid.NewGuid());
        var walletRepo = new RecordingWalletRepo(wallet);
        var invoice = TestInvoice(300_000L);
        var consumer = new WalletMutationConsumer(
            new StubMutationRepo(mutation), walletRepo, new StubInvoiceRepo(invoice));

        await consumer.ApplyAsync(mutation.Id);

        Assert.Equal(200_000L, wallet.AvailableAmount);
        Assert.Equal(WalletMutationStatus.Applied, mutation.Status);

        var movement = Assert.Single(walletRepo.Staged);
        Assert.Equal(WalletTransactionType.InvoicePayment, movement.Type);
        Assert.Equal(-300_000L, movement.Amount);       // negative — money out (titan-ops architecture/money-and-wallet.md#money-is-long-vnd)

        // The invoice becomes paid HERE, beside the debit, never at the button.
        Assert.Equal(InvoiceStatus.SettledByWallet, invoice.Status);
        Assert.Equal("wallet", invoice.PaidBy);

        // One commit: balance, ledger row, invoice flip and the mutation verdict are one transaction.
        Assert.Equal(1, walletRepo.SaveCount);
    }

    // The admin recorded outside money between the enqueue and the apply: the customer already paid, so the
    // wallet keeps its money. Only the verdict is saved.
    [Fact]
    public async Task Apply_DebitWhoseInvoiceIsAlreadySettled_FailsAndLeavesTheWallet()
    {
        var wallet = OpenWallet();
        wallet.Credit(500_000L);
        var invoice = TestInvoice(300_000L);
        invoice.MarkPaid(DateTimeOffset.UtcNow, "admin-1");
        var mutation = WalletMutation.ForDebit(wallet.Id, 300_000L, "Invoice", invoice.Id);
        var walletRepo = new RecordingWalletRepo(wallet);
        var consumer = new WalletMutationConsumer(
            new StubMutationRepo(mutation), walletRepo, new StubInvoiceRepo(invoice));

        await consumer.ApplyAsync(mutation.Id);

        Assert.Equal(500_000L, wallet.AvailableAmount);
        Assert.Equal(WalletMutationStatus.Failed, mutation.Status);
        Assert.Empty(walletRepo.Staged);
        Assert.Equal("admin-1", invoice.PaidBy);
        Assert.Equal(1, walletRepo.SaveCount);
    }

    // An invoice debit whose invoice is gone has nothing to pay.
    [Fact]
    public async Task Apply_DebitWhoseInvoiceIsGone_FailsAndLeavesTheWallet()
    {
        var wallet = OpenWallet();
        wallet.Credit(500_000L);
        var mutation = WalletMutation.ForDebit(wallet.Id, 300_000L, "Invoice", Guid.NewGuid());
        var walletRepo = new RecordingWalletRepo(wallet);
        var consumer = new WalletMutationConsumer(
            new StubMutationRepo(mutation), walletRepo, new StubInvoiceRepo(null));

        await consumer.ApplyAsync(mutation.Id);

        Assert.Equal(500_000L, wallet.AvailableAmount);
        Assert.Equal(WalletMutationStatus.Failed, mutation.Status);
        Assert.Empty(walletRepo.Staged);
    }

    private static Titan.Domain.Aggregates.Invoice.Invoice TestInvoice(long total)
    {
        var items = new List<Titan.Domain.Aggregates.Invoice.InvoiceItem>
        {
            Titan.Domain.Aggregates.Invoice.InvoiceItem.Create(
                Titan.Domain.Aggregates.UsageRecord.UsageResourceType.Compute,
                Guid.NewGuid(), "vm-1", total, "VND")
        };

        return Titan.Domain.Aggregates.Invoice.Invoice.Generate(
            Guid.NewGuid(), 2026, 9, DateTimeOffset.UtcNow.AddDays(-1), items, "VND");
    }
}

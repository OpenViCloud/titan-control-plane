using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Billing;
using InvoiceAggregate = Titan.Domain.Aggregates.Billing.Invoice.Invoice;

namespace Titan.Application.Services.Billing.Invoice;

// The tenant pay button and the admin settle button follow one rule, and handlers may not call each
// other (ReadWriteSeparationTests.Handlers_DoNotReferenceOtherHandlers), so the rule lives here
// (titan-ops architecture/code-structure.md#services-branch).
public static class InvoiceWalletSettlement
{
    /// Is this press a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat)? Yes when
    /// the wallet already paid the invoice or a debit for it is waiting. Not a repeat: an invoice paid
    /// outside Titan (MarkPaid, a different money route, still refused by SettleAsync) or a failed debit,
    /// which SettleAsync reopens.
    public static async Task<bool> IsAlreadyPaidFromWalletAsync(
        IWalletMutationRepository mutationRepo, InvoiceAggregate invoice, CancellationToken ct)
    {
        bool walletAlreadyPaid = invoice.Status == InvoiceStatus.SettledByWallet;
        if (walletAlreadyPaid)
        {
            return true;
        }

        WalletMutation? debit = await mutationRepo.GetForRefAsync(
            WalletMutationConsumer.InvoiceRefType, invoice.Id, ct);
        bool debitIsWaiting = debit != null && debit.Status == WalletMutationStatus.Pending;
        return debitIsWaiting;
    }

    /// The one place a request asks for a wallet debit to pay an invoice, shared by both buttons so
    /// neither can drift into a softer rule. The debit itself happens in WalletMutationConsumer.
    public static async Task SettleAsync(
        IWalletRepository walletRepo,
        IWalletMutationRepository mutationRepo,
        IWalletMutationPublisher walletBus,
        InvoiceAggregate invoice, CancellationToken ct)
    {
        if (invoice.Status != InvoiceStatus.New)
            throw new BusinessException("INVOICE_INVALID_STATE",
                $"Invoice {invoice.Id} is already settled (was {invoice.Status}).");

        // All or nothing, answered at the button (titan-ops architecture/money-and-wallet.md#human-confirmed-debit), so
        // the customer learns how much is missing. Not the guard: two payments can both pass this read, and
        // the consumer decides which is honoured (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
        var balance = await walletRepo.GetBalanceAsync(invoice.TenantId, ct);
        if (balance < invoice.Total)
            throw new BusinessException("INVOICE_WALLET_INSUFFICIENT",
                $"Wallet is short by {invoice.Total - balance} {invoice.Currency} " +
                $"for invoice {invoice.Id} (total {invoice.Total} {invoice.Currency}).");

        var wallet = await walletRepo.GetByTenantForUpdateAsync(invoice.TenantId, ct)
            ?? throw new ConflictException("INVOICE_PAYMENT_CONFLICT",
                $"Invoice {invoice.Id}: the wallet is no longer available; nothing was charged.");

        // The request only writes the mutation. Balance, ledger row and invoice status change together in
        // the consumer's transaction, so the invoice never reads "paid" while the money is still in the wallet.
        // An invoice has one debit row, ever (titan-ops features/billing/invoice/design.md#retry-after-failed-debit):
        // the first press adds it, a press after it failed reopens it.
        WalletMutation? debit = await mutationRepo.GetForRefForUpdateAsync(
            WalletMutationConsumer.InvoiceRefType, invoice.Id, ct);
        bool reopened = debit != null;
        if (debit == null)
        {
            debit = WalletMutation.ForDebit(
                wallet.Id, invoice.Total, WalletMutationConsumer.InvoiceRefType, invoice.Id);
            mutationRepo.Add(debit);
        }
        else
        {
            bool debitFailed = debit.Status == WalletMutationStatus.Failed;
            if (!debitFailed)
            {
                // Waiting or applied: another press got here after IsAlreadyPaidFromWalletAsync read it.
                return;
            }

            // The row asks for the same money from the same wallet, or it is not this payment to reopen.
            bool sameDebit = debit.Amount == invoice.Total && debit.WalletId == wallet.Id;
            if (!sameDebit)
            {
                throw new ConflictException("INVOICE_PAYMENT_CONFLICT",
                    $"Invoice {invoice.Id}: its failed debit asks {debit.Amount} from wallet {debit.WalletId}, " +
                    $"not {invoice.Total} from wallet {wallet.Id}; nothing was charged.");
            }

            debit.ReopenFailedDebit();
        }

        try
        {
            await walletRepo.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Two simultaneous presses both pass IsAlreadyPaidFromWalletAsync. The second one either collides
            // at UNIQUE(ref_type, ref_id) (both added the row) or loses the status token (both reopened it).
            // Losing to the other press's waiting or applied debit makes this press a repeat; anything else is
            // a real conflict. After a refused insert the row read back must be the other press's; after a
            // refused reopen it is this same row, as the other press left it.
            WalletMutation? existing = await mutationRepo.GetForRefAsync(
                WalletMutationConsumer.InvoiceRefType, invoice.Id, ct);
            bool anotherPressGotThereFirst = existing != null
                && existing.Status != WalletMutationStatus.Failed
                && (reopened || existing.Id != debit.Id);
            if (anotherPressGotThereFirst)
            {
                return;
            }

            throw new ConflictException("INVOICE_PAYMENT_CONFLICT",
                $"Invoice {invoice.Id}: the payment could not start; nothing was charged.");
        }

        // Post-commit notify, swallowed on purpose: the mutation is durable and the sweeper re-drives
        // anything the bus loses, so a failed publish costs latency, never money.
        await PublishQuietlyAsync(walletBus, debit, ct);

        // Nothing goes back (titan-ops architecture/http-contract.md#commands-return-no-data). A caller reading the
        // invoice back sees it still New with PaymentState=Processing: payment accepted, money moving.
    }

    private static async Task PublishQuietlyAsync(
        IWalletMutationPublisher walletBus, WalletMutation mutation, CancellationToken ct)
    {
        try
        {
            await walletBus.PublishAsync(new WalletMutationNotification(mutation.Id, mutation.WalletId), ct);
        }
        catch
        {
            // Deliberately silent: the sweeper is the safety net (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
        }
    }
}

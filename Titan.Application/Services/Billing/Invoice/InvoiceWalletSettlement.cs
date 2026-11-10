using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using InvoiceAggregate = Titan.Domain.Aggregates.Invoice.Invoice;

namespace Titan.Application.Services.Billing.Invoice;

// The tenant pay button and the admin settle button follow one rule, and handlers may not call each other
// (ReadWriteSeparationTests.Handlers_DoNotReferenceOtherHandlers), so what surrounds the rule lives here: the repeat, the
// save, the notify. The rule is InvoiceWalletPayment.
public static class InvoiceWalletSettlement
{
    /// Is this press a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat)? Yes when
    /// the wallet already paid the invoice or a debit for it is waiting. Not a repeat: an invoice paid
    /// outside Titan (MarkPaid, a different money route, still refused by RequestAsync) or a failed debit,
    /// which RequestAsync reopens.
    public static async Task<bool> IsAlreadyPaidFromWalletAsync(
        IWalletMutationRepository mutationRepo, InvoiceAggregate invoice, CancellationToken ct)
    {
        if (invoice.IsSettledByWallet)
        {
            return true;
        }
        return await IsDebitWaitingAsync(mutationRepo, invoice, ct);
    }

    /// A debit for the invoice is queued and not yet applied: the invoice still reads New until the consumer applies it.
    public static async Task<bool> IsDebitWaitingAsync(
        IWalletMutationRepository mutationRepo, InvoiceAggregate invoice, CancellationToken ct)
    {
        WalletMutation? debit = await mutationRepo.GetForRefAsync(WalletMutation.InvoiceRefType, invoice.Id, ct);
        return debit is { IsPending: true };
    }

    /// What surrounds the rule of InvoiceWalletPayment for both buttons, so neither can drift into a softer one: the save
    /// that commits the debit row, a press that lost to another, and the notify.
    public static async Task SettleAsync(
        InvoiceWalletPayment payment,
        IWalletRepository walletRepo,
        IWalletMutationRepository mutationRepo,
        IWalletMutationPublisher walletBus,
        InvoiceAggregate invoice, CancellationToken ct)
    {
        WalletMutation? debit = null;
        try
        {
            debit = await payment.PayAsync(invoice, ct);
            await walletRepo.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // Two simultaneous presses both pass IsAlreadyPaidFromWalletAsync. The second one finds the first's row
            // (RequestAsync refuses, no debit), collides at UNIQUE(ref_type, ref_id) (both added the row), or loses the
            // status token (both reopened it). Losing to the other press's waiting or applied debit makes this press a
            // repeat; anything else is a real conflict. After a refused insert the row read back must be the other
            // press's; after a refused reopen it is this same row, as the other press left it.
            WalletMutation? existing = await mutationRepo.GetForRefAsync(
                WalletMutation.InvoiceRefType, invoice.Id, ct);
            bool readBackOwnRefusedInsert = debit != null && !debit.WasReopened
                && existing != null && existing.Id == debit.Id;
            bool anotherPressGotThereFirst = existing != null
                && existing.Status != WalletMutationStatus.Failed
                && !readBackOwnRefusedInsert;
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

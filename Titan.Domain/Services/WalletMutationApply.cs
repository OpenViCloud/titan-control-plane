using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Domain.Services;

/// The one way a wallet mutation reaches the balance (titan-ops architecture/money-and-wallet.md#single-writer-outbox). The
/// rule spans the mutation, the wallet, its ledger and the invoice a debit pays, so it is a domain service;
/// WalletMutation.MarkApplied and Invoice.SettleFromWallet are internal. The caller gives a Pending mutation and saves
/// once after this, so balance, ledger row, invoice and verdict commit together.
public class WalletMutationApply(IWalletRepository wallets, IInvoiceRepository invoices, WalletLedger ledger)
{
    public async Task ApplyAsync(WalletMutation mutation, CancellationToken ct = default)
    {
        if (mutation.Status != WalletMutationStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Mutation {mutation.Id} is {mutation.Status}; only a pending mutation can be applied.");
        }

        Wallet? wallet = await wallets.GetByIdForUpdateAsync(mutation.WalletId, ct);
        if (wallet == null)
        {
            throw new NotFoundException("WALLET_NOT_FOUND",
                $"Mutation {mutation.Id} names wallet {mutation.WalletId}, which does not exist; a mutation " +
                "should only ever be enqueued against an opened wallet.");
        }

        // A Credit always comes from a confirmed TopUp and a Debit from an invoice payment, so the ledger
        // call follows Type. The default refuses an unknown member instead of dropping it silently.
        switch (mutation.Type)
        {
            case WalletMutationType.Credit:
                ledger.Credit(wallet, WalletTransactionType.TopUp, mutation.Amount, mutation.RefId);
                mutation.MarkApplied();
                break;

            case WalletMutationType.Debit:
                // The authoritative funds check (titan-ops architecture/money-and-wallet.md#single-writer-outbox): two
                // payments can both pass the pre-check at the button, and only one can be honoured.
                // A test before ledger.Debit, not a catch around it: too little money is this mutation's verdict, not an error.
                if (wallet.AvailableAmount < mutation.Amount)
                {
                    mutation.MarkFailed(
                        $"Wallet {wallet.Id} holds {wallet.AvailableAmount}; this debit needs {mutation.Amount}. " +
                        "The balance was sufficient when the payment was requested and was spent before it " +
                        "was applied.");
                    break;      // no debit, no ledger row, no invoice flip — only the verdict is saved
                }

                // The invoice is read before the wallet moves: an admin MarkPaid between the enqueue and now means
                // the customer already paid. Its Status is a concurrency token, so a MarkPaid committing after
                // this read fails the save instead of being paid twice.
                Invoice? invoice = null;
                bool paysAnInvoice = mutation.RefType == WalletMutation.InvoiceRefType;
                if (paysAnInvoice)
                {
                    invoice = await invoices.GetForUpdateAsync(mutation.RefId, ct);
                    bool invoiceStillOwed = invoice != null && invoice.Status == InvoiceStatus.New;
                    if (!invoiceStillOwed)
                    {
                        string found = "missing";
                        if (invoice != null)
                        {
                            found = invoice.Status.ToString();
                        }
                        mutation.MarkFailed(
                            $"Invoice {mutation.RefId} is no longer owed (was {found}); the wallet was not charged.");
                        break;  // no debit, no ledger row — only the verdict is saved
                    }
                }

                ledger.Debit(wallet, mutation.Amount, mutation.RefId);
                if (invoice != null)
                {
                    // The invoice reaches SettledByWallet in the debit's transaction, not at the button:
                    // flipping it at request time would let it read "paid" before the money moved.
                    invoice.SettleFromWallet(DateTimeOffset.UtcNow);
                }
                mutation.MarkApplied();
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mutation), mutation.Type, "Unknown wallet mutation type; cannot apply.");
        }
    }
}

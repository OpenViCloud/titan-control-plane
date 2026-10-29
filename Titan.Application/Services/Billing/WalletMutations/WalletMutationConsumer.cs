using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Billing;
using InvoiceAggregate = Titan.Domain.Aggregates.Billing.Invoice.Invoice;

namespace Titan.Application.Services.Billing.WalletMutations;

// The single-writer consumer (titan-ops architecture/money-and-wallet.md#single-writer-outbox): the one place a
// wallet mutation is applied to the balance. The subscriber and the sweeper both call it; the wallet version
// token catches two applies that read the wallet at the same time.
//
// A non-Pending mutation is a no-op, so a redelivered notify is safe. The check reads the mutation this
// context loaded, so it only sees another context's apply when the context is fresh: every caller gives each
// apply its own scope (titan-ops features/billing/wallet/design.md#wallet-mutation-flow).
public interface IWalletMutationConsumer
{
    /// Commits the balance change, the ledger row and the mutation's MarkApplied in one transaction
    /// (titan-ops architecture/money-and-wallet.md#wallet-invariant-in-aggregate).
    Task ApplyAsync(Guid mutationId, CancellationToken ct = default);
}

public class WalletMutationConsumer(
    IWalletMutationRepository mutationRepo,
    IWalletRepository walletRepo,
    IInvoiceRepository invoiceRepo) : IWalletMutationConsumer
{
    public async Task ApplyAsync(Guid mutationId, CancellationToken ct = default)
    {
        var mutation = await mutationRepo.GetForUpdateAsync(mutationId, ct);
        if (mutation is null) return;                  // a notify for a mutation that never landed

        // Only a Pending mutation has work left: a redelivered notify returns here without touching the wallet.
        if (mutation.Status != WalletMutationStatus.Pending) return;

        var wallet = await walletRepo.GetByIdForUpdateAsync(mutation.WalletId, ct)
            ?? throw new NotFoundException("WALLET_NOT_FOUND",
                $"Mutation {mutation.Id} names wallet {mutation.WalletId}, which does not exist; a mutation " +
                "should only ever be enqueued against an opened wallet.");

        // A Credit always comes from a confirmed TopUp and a Debit from an invoice payment, so the ledger
        // factory follows Type. The default refuses an unknown member instead of dropping it silently.
        switch (mutation.Type)
        {
            case WalletMutationType.Credit:
                wallet.Credit(mutation.Amount);
                walletRepo.AddTransaction(
                    WalletTransaction.ForTopUp(wallet.Id, mutation.Amount, mutation.RefId, wallet.AvailableAmount));
                mutation.MarkApplied();
                break;

            case WalletMutationType.Debit:
                // The authoritative funds check (titan-ops architecture/money-and-wallet.md#single-writer-outbox): two
                // payments can both pass the pre-check at the button, and only one can be honoured.
                // A test before Wallet.Debit, not a catch around it: once Debit throws, the ChangeTracker may
                // hold half-made changes that the MarkFailed SaveChanges would commit along with the verdict.
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
                InvoiceAggregate? invoice = null;
                bool paysAnInvoice = mutation.RefType == InvoiceRefType;
                if (paysAnInvoice)
                {
                    invoice = await invoiceRepo.GetForUpdateAsync(mutation.RefId, ct);
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

                wallet.Debit(mutation.Amount);
                walletRepo.AddTransaction(
                    WalletTransaction.ForInvoicePayment(wallet.Id, mutation.Amount, mutation.RefId, wallet.AvailableAmount));
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

        // Both repositories share the DbContext, so one SaveChanges commits balance, ledger row and status.
        // A double-apply that slipped past the status check fails here on the ledger UNIQUE(type, ref_id) or the
        // version token; the failed save does not undo the balance change, so the context must not be reused.
        await walletRepo.SaveChangesAsync(ct);
    }

    /// Shared by the enqueuer and this consumer so the two cannot drift apart.
    public const string InvoiceRefType = "Invoice";
}

using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Domain.Services;

/// The one way a request asks a wallet to pay an invoice, for the tenant's button and the admin's alike. The rule spans
/// the invoice, the wallet and the debit row, so it is a domain service; WalletMutation.Create and ReopenFailedDebit are
/// internal. It only writes the row: balance, ledger row and invoice status change together in WalletMutationApply, so
/// the invoice never reads "paid" while the money is still in the wallet. The row commits in the caller's save.
public class InvoiceWalletPayment(IWalletRepository wallets, IWalletMutationRepository mutations)
{
    public async Task<WalletMutation> RequestAsync(Invoice invoice, CancellationToken ct = default)
    {
        if (invoice.Status != InvoiceStatus.New)
        {
            throw new BusinessException("INVOICE_INVALID_STATE",
                $"Invoice {invoice.Id} is already settled (was {invoice.Status}).");
        }

        // All or nothing, answered at the button (titan-ops architecture/money-and-wallet.md#human-confirmed-debit), so
        // the customer learns how much is missing. Not the guard: two payments can both pass this read, and
        // WalletMutationApply decides which is honoured (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
        long balance = await wallets.GetBalanceAsync(invoice.TenantId, ct);
        if (balance < invoice.Total)
        {
            throw new BusinessException("INVOICE_WALLET_INSUFFICIENT",
                $"Wallet is short by {invoice.Total - balance} {invoice.Currency} " +
                $"for invoice {invoice.Id} (total {invoice.Total} {invoice.Currency}).");
        }

        Wallet? wallet = await wallets.GetByTenantForUpdateAsync(invoice.TenantId, ct);
        if (wallet == null)
        {
            throw new ConflictException("INVOICE_PAYMENT_CONFLICT",
                $"Invoice {invoice.Id}: the wallet is no longer available; nothing was charged.");
        }

        // An invoice has one debit row, ever (titan-ops features/billing/invoice/design.md#retry-after-failed-debit):
        // the first press adds it, a press after it failed reopens it.
        WalletMutation? debit = await mutations.GetForRefForUpdateAsync(WalletMutation.InvoiceRefType, invoice.Id, ct);
        if (debit == null)
        {
            debit = WalletMutation.Create(WalletMutationType.Debit, wallet.Id, invoice.Total, WalletMutation.InvoiceRefType, invoice.Id);
            mutations.Add(debit);
            return debit;
        }

        // Waiting or applied: another press got here after the caller checked for a repeat. The caller answers it
        // like every press that lost to another (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        if (debit.Status != WalletMutationStatus.Failed)
        {
            throw new ConcurrencyConflictException("INVOICE_DEBIT_ALREADY_ASKED",
                $"Invoice {invoice.Id}: its debit is already {debit.Status}.");
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
        return debit;
    }
}

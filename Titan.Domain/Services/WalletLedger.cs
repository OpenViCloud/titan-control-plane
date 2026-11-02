using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Repositories;

namespace Titan.Domain.Services;

/// The one way money moves (titan-ops architecture/money-and-wallet.md#signed-ledger): a balance change and its ledger
/// row are one call. The rule spans two aggregates, so it is a domain service; it holds the repository so a caller
/// cannot change the balance and forget the row. The row commits in the balance's SaveChanges.
public class WalletLedger(IWalletTransactionRepository transactions)
{
    public void Credit(Wallet wallet, WalletTransactionType type, long amount, Guid refId)
    {
        if (type == WalletTransactionType.InvoicePayment)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "An invoice payment takes money out of the wallet.");
        }
        wallet.Credit(amount);
        transactions.Add(WalletTransaction.Create(type, wallet.Id, amount, refId, wallet.AvailableAmount));
    }

    public void Debit(Wallet wallet, long amount, Guid invoiceId)
    {
        wallet.Debit(amount);
        transactions.Add(WalletTransaction.Create(
            WalletTransactionType.InvoicePayment, wallet.Id, amount, invoiceId, wallet.AvailableAmount));
    }
}

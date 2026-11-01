
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Wallet;

/// Append-only ledger row; SUM(Amount) over a wallet must equal its AvailableAmount
/// (titan-ops architecture/money-and-wallet.md#signed-ledger). The factories apply the sign, not callers: a caller
/// that forgot a minus would record a spend as income, and the balance would still reconcile against its
/// own wrong ledger.
public class WalletTransaction : ChildEntity
{
    public Guid Id { get; private set; }
    public Guid WalletId { get; private set; }

    /// Signed whole VND: positive for money in, negative for money out. The one money amount that may be
    /// negative; reading it like the other `Amount`s would turn a debit into a credit.
    public long Amount { get; private set; }

    public WalletTransactionType Type { get; private set; }

    /// A TopUpId (TopUp and Promo) or an InvoiceId, per Type.
    public Guid RefId { get; private set; }

    /// The wallet's balance right after this movement, as the aggregate computed it; makes a statement line
    /// self-describing.
    public long BalanceAfter { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private WalletTransaction() { }

    /// Takes the positive amount of money and applies the type's sign.
    public static WalletTransaction Create(
        WalletTransactionType type, Guid walletId, long amount, Guid refId, long balanceAfter)
    {
        // A zero movement or a negative "money in" would both reconcile fine and both be wrong.
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A wallet movement must be a positive amount of money.");
        long signedAmount = type switch
        {
            WalletTransactionType.TopUp or WalletTransactionType.Promo => amount,
            WalletTransactionType.InvoicePayment => -amount,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "A wallet movement of a type with no sign."),
        };
        return new WalletTransaction
        {
            Id = Guid.NewGuid(),
            WalletId = walletId,
            Amount = signedAmount,
            Type = type,
            RefId = refId,
            BalanceAfter = balanceAfter,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}

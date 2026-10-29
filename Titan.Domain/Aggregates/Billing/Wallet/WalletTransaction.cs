namespace Titan.Domain.Aggregates.Billing.Wallet;

/// Append-only ledger row; SUM(Amount) over a wallet must equal its AvailableAmount
/// (titan-ops architecture/money-and-wallet.md#signed-ledger). The factories apply the sign, not callers: a caller
/// that forgot a minus would record a spend as income, and the balance would still reconcile against its
/// own wrong ledger.
public class WalletTransaction
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

    public static WalletTransaction ForTopUp(Guid walletId, long amount, Guid topUpId, long balanceAfter) =>
        MoneyIn(walletId, amount, WalletTransactionType.TopUp, topUpId, balanceAfter);

    public static WalletTransaction ForPromo(Guid walletId, long amount, Guid promotionalCreditId, long balanceAfter) =>
        MoneyIn(walletId, amount, WalletTransactionType.Promo, promotionalCreditId, balanceAfter);

    /// Takes the positive invoice total and records it negative.
    public static WalletTransaction ForInvoicePayment(Guid walletId, long amount, Guid invoiceId, long balanceAfter)
    {
        Require(amount);
        return Create(walletId, -amount, WalletTransactionType.InvoicePayment, invoiceId, balanceAfter);
    }

    private static WalletTransaction MoneyIn(
        Guid walletId, long amount, WalletTransactionType type, Guid refId, long balanceAfter)
    {
        Require(amount);
        return Create(walletId, amount, type, refId, balanceAfter);
    }

    /// A zero movement or a negative "money in" would both reconcile fine and both be wrong.
    private static void Require(long amount)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "A wallet movement must be a positive amount of money.");
    }

    private static WalletTransaction Create(
        Guid walletId, long signedAmount, WalletTransactionType type, Guid refId, long balanceAfter) => new()
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

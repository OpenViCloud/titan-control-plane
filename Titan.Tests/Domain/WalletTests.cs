using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Wallet;

namespace Titan.Tests.Domain;

// A tenant's wallet is ONE entity holding what they can spend, and its history is ONE append-only ledger of
// signed movements (titan-ops architecture/money-and-wallet.md#wallet-one-balance); amounts are whole VND (long).
//
// What is worth testing without a database: the SIGN is applied by the factory, not by the caller.
// That is the invariant that keeps "paid an invoice" from ever landing in the ledger as income.
public class WalletTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Open_StartsEmpty()
    {
        var wallet = Wallet.Open(Tenant, "VND");

        Assert.Equal(Tenant, wallet.TenantId);
        Assert.Equal("VND", wallet.Currency);
        Assert.Equal(0L, wallet.AvailableAmount);
        Assert.NotEqual(Guid.Empty, wallet.Id);
        Assert.Equal(wallet.CreatedAt, wallet.UpdatedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Transactions_RejectNonPositiveAmounts(long bad)
    {
        var walletId = Guid.NewGuid();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WalletTransaction.ForTopUp(walletId, bad, Guid.NewGuid(), balanceAfter: 0L));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WalletTransaction.ForPromo(walletId, bad, Guid.NewGuid(), balanceAfter: 0L));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WalletTransaction.ForInvoicePayment(walletId, bad, Guid.NewGuid(), balanceAfter: 0L));
    }

    [Fact]
    public void ForTopUp_IsMoneyIn()
    {
        var walletId = Guid.NewGuid();
        var topUpId = Guid.NewGuid();

        var txn = WalletTransaction.ForTopUp(walletId, 500_000L, topUpId, balanceAfter: 500_000L);

        Assert.Equal(walletId, txn.WalletId);
        Assert.Equal(500_000L, txn.Amount);           // positive — money in
        Assert.Equal(WalletTransactionType.TopUp, txn.Type);
        Assert.Equal(topUpId, txn.RefId);
        Assert.Equal(500_000L, txn.BalanceAfter);
    }

    [Fact]
    public void ForPromo_IsMoneyIn_AndDistinguishableFromTopUp()
    {
        // The Type split exists for accounting: money the customer paid (deferred revenue) must stay separable
        // from credit Titan gave away. Spending never reads it (titan-ops architecture/money-and-wallet.md#credit-is-money).
        var requestId = Guid.NewGuid();

        var txn = WalletTransaction.ForPromo(Guid.NewGuid(), 100_000L, requestId, balanceAfter: 100_000L);

        Assert.Equal(100_000L, txn.Amount);
        Assert.Equal(WalletTransactionType.Promo, txn.Type);
        Assert.Equal(requestId, txn.RefId);
    }

    [Fact]
    public void ForInvoicePayment_IsMoneyOut_SignAppliedByTheFactory()
    {
        // The caller passes what the invoice cost — a POSITIVE number — and gets back a NEGATIVE
        // movement. No call site can spell a spend as income by forgetting a minus sign.
        var invoiceId = Guid.NewGuid();

        var txn = WalletTransaction.ForInvoicePayment(Guid.NewGuid(), 30_000L, invoiceId, balanceAfter: 70_000L);

        Assert.Equal(-30_000L, txn.Amount);
        Assert.Equal(WalletTransactionType.InvoicePayment, txn.Type);
        Assert.Equal(invoiceId, txn.RefId);
        Assert.Equal(70_000L, txn.BalanceAfter);
    }

    [Fact]
    public void Ledger_SumsToTheBalanceItDescribes()
    {
        // The balance is the sum of the ledger (no DB constraint enforces it); here we pin that the SIGNS make
        // the arithmetic work at all.
        var walletId = Guid.NewGuid();
        var movements = new[]
        {
            WalletTransaction.ForTopUp(walletId, 500_000L, Guid.NewGuid(), 500_000L),
            WalletTransaction.ForPromo(walletId, 10_000L, Guid.NewGuid(), 510_000L),
            WalletTransaction.ForInvoicePayment(walletId, 158L, Guid.NewGuid(), 509_842L),
        };

        Assert.Equal(509_842L, movements.Sum(m => m.Amount));
        Assert.Equal(movements[^1].BalanceAfter, movements.Sum(m => m.Amount));
    }

    [Fact]
    public void Debit_BeyondTheBalance_IsRefused_AndLeavesTheBalance()
    {
        var wallet = Wallet.Open(Tenant, "VND");
        wallet.Credit(50_000L);
        long versionBefore = wallet.Version;

        var refused = Assert.Throws<Titan.Domain.Exceptions.BusinessException>(() => wallet.Debit(50_001L));

        Assert.Equal("WALLET_INSUFFICIENT_FUNDS", refused.Code);
        Assert.Equal(50_000L, wallet.AvailableAmount);
        Assert.Equal(versionBefore, wallet.Version);
    }

    [Fact]
    public void Debit_OfTheWholeBalance_LeavesZero_AndMovesTheVersion()
    {
        var wallet = Wallet.Open(Tenant, "VND");
        wallet.Credit(50_000L);
        long versionBefore = wallet.Version;

        wallet.Debit(50_000L);

        Assert.Equal(0L, wallet.AvailableAmount);
        Assert.Equal(versionBefore + 1, wallet.Version);
    }
}

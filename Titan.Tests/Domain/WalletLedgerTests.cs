using System.Reflection;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Tests.Domain;

// titan-ops architecture/money-and-wallet.md#signed-ledger: a balance change and its ledger row are one call.
// Silent about the commit itself; WalletLedgerDbTests reads both back from Postgres.
public class WalletLedgerTests
{
    private sealed class RecordingTransactions : IWalletTransactionRepository
    {
        public List<WalletTransaction> Added { get; } = [];
        public void Add(WalletTransaction transaction) => Added.Add(transaction);
        public Task<IReadOnlyList<WalletTransaction>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private readonly RecordingTransactions _transactions = new();
    private readonly Wallet _wallet = Wallet.Open(Guid.NewGuid(), "VND");

    private WalletLedger Ledger() => new(_transactions);

    [Theory]
    [InlineData(WalletTransactionType.TopUp)]
    [InlineData(WalletTransactionType.Promo)]
    public void Credit_MovesTheBalance_AndAddsOnePositiveRow(WalletTransactionType type)
    {
        var refId = Guid.NewGuid();

        Ledger().Credit(_wallet, type, 500L, refId);

        Assert.Equal(500L, _wallet.AvailableAmount);
        WalletTransaction row = Assert.Single(_transactions.Added);
        Assert.Equal(type, row.Type);
        Assert.Equal(_wallet.Id, row.WalletId);
        Assert.Equal(500L, row.Amount);
        Assert.Equal(refId, row.RefId);
        Assert.Equal(500L, row.BalanceAfter);
    }

    [Fact]
    public void Debit_MovesTheBalance_AndAddsOneNegativeRow()
    {
        var invoiceId = Guid.NewGuid();
        Ledger().Credit(_wallet, WalletTransactionType.TopUp, 500L, Guid.NewGuid());

        Ledger().Debit(_wallet, 200L, invoiceId);

        Assert.Equal(300L, _wallet.AvailableAmount);
        Assert.Equal(2, _transactions.Added.Count);
        WalletTransaction row = _transactions.Added[1];
        Assert.Equal(WalletTransactionType.InvoicePayment, row.Type);
        Assert.Equal(-200L, row.Amount);
        Assert.Equal(invoiceId, row.RefId);
        Assert.Equal(300L, row.BalanceAfter);
    }

    [Fact]
    public void Debit_OfTheWholeBalance_LeavesZero()
    {
        Ledger().Credit(_wallet, WalletTransactionType.TopUp, 500L, Guid.NewGuid());

        Ledger().Debit(_wallet, 500L, Guid.NewGuid());

        Assert.Equal(0L, _wallet.AvailableAmount);
        Assert.Equal(0L, _transactions.Added[1].BalanceAfter);
    }

    [Fact]
    public void Credit_AsAnInvoicePayment_IsRefused_AndChangesNothing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Ledger().Credit(_wallet, WalletTransactionType.InvoicePayment, 500L, Guid.NewGuid()));

        Assert.Equal(0L, _wallet.AvailableAmount);
        Assert.Empty(_transactions.Added);
    }

    [Fact]
    public void Debit_BeyondTheBalance_IsRefused_AndChangesNothing()
    {
        Ledger().Credit(_wallet, WalletTransactionType.TopUp, 500L, Guid.NewGuid());
        int versionBefore = _wallet.Version;

        var refused = Assert.Throws<BusinessException>(() => Ledger().Debit(_wallet, 501L, Guid.NewGuid()));

        Assert.Equal("WALLET_INSUFFICIENT_FUNDS", refused.Code);
        Assert.Equal(500L, _wallet.AvailableAmount);
        Assert.Equal(versionBefore, _wallet.Version);
        Assert.Single(_transactions.Added);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveAmounts_AreRefused_AndChangeNothing(long bad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Ledger().Credit(_wallet, WalletTransactionType.TopUp, bad, Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Ledger().Debit(_wallet, bad, Guid.NewGuid()));

        Assert.Equal(0L, _wallet.AvailableAmount);
        Assert.Empty(_transactions.Added);
    }

    // The compiler keeps Titan.Application off these three, so the ledger is the only way to move money.
    [Fact]
    public void OnlyTheLedgerMovesTheBalance()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] publicMethods = [.. typeof(Wallet).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(Wallet.Open), publicMethods);
        Assert.DoesNotContain(nameof(Wallet.Credit), publicMethods);
        Assert.DoesNotContain(nameof(Wallet.Debit), publicMethods);

        string[] publicFactories = [.. typeof(WalletTransaction).GetMethods(surface).Select(m => m.Name)];
        Assert.DoesNotContain(nameof(WalletTransaction.Create), publicFactories);
    }

    // The ledger has no upper bound, so it is its own aggregate: no property, field or method of the wallet carries a row.
    [Fact]
    public void WalletHoldsNoLedgerRow()
    {
        const BindingFlags everything = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MemberInfo[] members = typeof(Wallet).GetMembers(everything);
        Assert.NotEmpty(members);

        var offenders = members.Where(m => Mentions(m)).Select(m => m.Name).ToList();

        Assert.True(offenders.Count == 0, "Wallet carries a ledger row: " + string.Join(", ", offenders));
    }

    private static bool Mentions(MemberInfo member)
    {
        Type[] types = member switch
        {
            PropertyInfo property => [property.PropertyType],
            FieldInfo field => [field.FieldType],
            MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(p => p.ParameterType)],
            _ => [],
        };
        return types.Any(t => t == typeof(WalletTransaction) || t.GetGenericArguments().Contains(typeof(WalletTransaction)));
    }
}

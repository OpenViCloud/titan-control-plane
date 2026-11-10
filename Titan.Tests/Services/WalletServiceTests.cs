using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Queries.Billing.Wallet;
using Titan.Application.Queries.Billing.Wallet.Contracts;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

// The wallet itself: read its balance, read its statement. The real Wallet aggregate over a mocked repository.
// Opening it is TenantActivation's (TenantActivationTests).
public class WalletServiceTests
{
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<IWalletTransactionRepository> _transactions = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private static readonly Guid Tenant = Guid.NewGuid();

    // Facade over two query handlers: every method forwards to exactly one handler and adds nothing.
    private WalletFacade CreateService() => new(_walletRepo.Object, _transactions.Object, _currentUser.Object);

    private sealed class WalletFacade(
        IWalletRepository walletRepo, IWalletTransactionRepository transactions, ICurrentUser currentUser)
    {
        public Task<WalletQueryResult> GetMyWalletAsync(CancellationToken ct = default)
            => new GetWalletQueryHandler(walletRepo, currentUser).HandleAsync(new GetWalletQuery(), ct);

        public Task<WalletStatementQueryResult> GetMyStatementAsync(CancellationToken ct = default)
            => new GetWalletStatementQueryHandler(transactions, currentUser).HandleAsync(new GetWalletStatementQuery(), ct);
    }
    private void AsTenant() => _currentUser.SetupGet(u => u.TenantId).Returns(Tenant);

    // ── GetMyWalletAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetMyWallet_ReturnsBalance()
    {
        AsTenant();
        _walletRepo.Setup(r => r.GetBalanceAsync(Tenant, default)).ReturnsAsync(30L);

        var wallet = await CreateService().GetMyWalletAsync();

        Assert.Equal(30m, wallet.Balance);
        Assert.Equal("VND", wallet.Currency);
    }

    // A tenant who has never had money reads 0, not an error and not a created wallet.
    [Fact]
    public async Task GetMyWallet_NeverHadMoney_ReturnsZero()
    {
        AsTenant();
        _walletRepo.Setup(r => r.GetBalanceAsync(Tenant, default)).ReturnsAsync(0L);

        var wallet = await CreateService().GetMyWalletAsync();

        Assert.Equal(0m, wallet.Balance);
    }

    // ── GetMyStatementAsync ────────────────────────────────────

    // Every movement, in and out, chronologically, each carrying the balance the wallet
    // reached, from ONE table.
    [Fact]
    public async Task GetMyStatement_ListsInflowsAndSpends_WithRunningBalance()
    {
        AsTenant();
        var t0 = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var wallet = Guid.NewGuid();
        _transactions.Setup(r => r.ListByTenantAsync(Tenant, default)).ReturnsAsync([
            At(WalletTransaction.Create(WalletTransactionType.Promo, wallet, 100L, Guid.NewGuid(), 100L), t0),
            At(WalletTransaction.Create(WalletTransactionType.TopUp, wallet, 900000L, Guid.NewGuid(), 900100L), t0.AddDays(1)),
            At(WalletTransaction.Create(WalletTransactionType.InvoicePayment, wallet, 40L, Guid.NewGuid(), 900060L), t0.AddDays(2)),
        ]);

        var st = await CreateService().GetMyStatementAsync();

        Assert.Equal(3, st.Lines.Count);
        Assert.Equal(0m, st.OpeningBalance);
        Assert.Equal("Promo", st.Lines[0].Type);
        Assert.Equal(100m, st.Lines[0].Amount);
        Assert.Equal(100m, st.Lines[0].RunningBalance);
        Assert.Equal("TopUp", st.Lines[1].Type);
        Assert.Equal(900000m, st.Lines[1].Amount);
        Assert.Equal(900100m, st.Lines[1].RunningBalance);
        Assert.Equal("Spend", st.Lines[2].Type);         // the customer's word, not InvoicePayment
        Assert.Equal(-40m, st.Lines[2].Amount);
        Assert.Equal(900060m, st.Lines[2].RunningBalance);
        Assert.Equal(900060m, st.ClosingBalance);        // closes at the wallet balance
        Assert.Equal("VND", st.Currency);
    }

    // A spend is dated when the money left, which is when the button was pressed (titan-ops architecture/money-and-wallet.md#human-confirmed-debit).
    [Fact]
    public async Task GetMyStatement_SpendIsDatedWhenTheMoneyLeft()
    {
        AsTenant();
        var t0 = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var wallet = Guid.NewGuid();
        _transactions.Setup(r => r.ListByTenantAsync(Tenant, default)).ReturnsAsync([
            At(WalletTransaction.Create(WalletTransactionType.TopUp, wallet, 500L, Guid.NewGuid(), 500L), t0),
            At(WalletTransaction.Create(WalletTransactionType.InvoicePayment, wallet, 200L, Guid.NewGuid(), 300L), t0.AddDays(4)),
        ]);

        var st = await CreateService().GetMyStatementAsync();

        Assert.Equal(2, st.Lines.Count);
        Assert.Equal("TopUp", st.Lines[0].Type);
        Assert.Equal(500m, st.Lines[0].RunningBalance);
        Assert.Equal("Spend", st.Lines[1].Type);
        Assert.Equal(-200m, st.Lines[1].Amount);
        Assert.Equal(t0.AddDays(4), st.Lines[1].At);
        Assert.Equal(300m, st.ClosingBalance);
    }

    // A tenant with no activity gets a zero, empty statement, not an error.
    [Fact]
    public async Task GetMyStatement_NoActivity_ReturnsZero_Empty_NoError()
    {
        AsTenant();
        _transactions.Setup(r => r.ListByTenantAsync(Tenant, default)).ReturnsAsync([]);

        var st = await CreateService().GetMyStatementAsync();

        Assert.Empty(st.Lines);
        Assert.Equal(0m, st.OpeningBalance);
        Assert.Equal(0m, st.ClosingBalance);
    }

    /// When a movement happened must be STATED, never inferred from the order these were built:
    /// DateTimeOffset.UtcNow is coarser than the gap between two back-to-back constructor calls.
    private static WalletTransaction At(WalletTransaction txn, DateTimeOffset at)
    {
        typeof(WalletTransaction).GetProperty(nameof(WalletTransaction.CreatedAt))!.SetValue(txn, at);
        return txn;
    }
}

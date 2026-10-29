using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Commands.Billing.Wallet;
using Titan.Application.Commands.Billing.Wallet.Contracts;
using Titan.Application.Queries.Billing.Wallet;
using Titan.Application.Queries.Billing.Wallet.Contracts;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Billing;

namespace Titan.Tests.Services;

// The wallet itself: open it (at activation), read its balance, read its statement. The real Wallet
// aggregate over a mocked repository; a concurrent open is simulated by making SaveChangesAsync throw.
public class WalletServiceTests
{
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();
    private readonly CreditOptions _options = new() { MaxGrantAmount = 100_000_000m, Currency = "VND" };
    private static readonly Guid Tenant = Guid.NewGuid();

    public WalletServiceTests() =>
        _walletRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

    // Facade over one command and two query handlers: every method forwards to exactly one handler and
    // adds nothing.
    private WalletFacade CreateService() => new(_walletRepo.Object, _currentUser.Object, _options);

    private sealed class WalletFacade(IWalletRepository walletRepo, ICurrentUser currentUser, CreditOptions options)
    {
        public Task OpenForTenantAsync(Guid tenantId, CancellationToken ct = default)
            => new OpenWalletCommandHandler(walletRepo, options).HandleAsync(new OpenWalletCommand(tenantId), ct);

        public Task<WalletQueryResult> GetMyWalletAsync(CancellationToken ct = default)
            => new GetWalletQueryHandler(walletRepo, currentUser, options).HandleAsync(new GetWalletQuery(), ct);

        public Task<WalletStatementQueryResult> GetMyStatementAsync(CancellationToken ct = default)
            => new GetWalletStatementQueryHandler(walletRepo, currentUser, options).HandleAsync(new GetWalletStatementQuery(), ct);
    }
    private void AsTenant() => _currentUser.SetupGet(u => u.TenantId).Returns(Tenant);

    // ── OpenForTenantAsync ───────────────────────────────────

    // The activation path opens an EMPTY wallet in the configured currency and persists it.
    [Fact]
    public async Task OpenForTenant_NoWallet_OpensEmptyWallet_AndSaves()
    {
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync((Wallet?)null);
        Wallet? opened = null;
        _walletRepo.Setup(r => r.Add(It.IsAny<Wallet>())).Callback<Wallet>(w => opened = w);

        await CreateService().OpenForTenantAsync(Tenant);

        Assert.NotNull(opened);
        Assert.Equal(Tenant, opened!.TenantId);
        Assert.Equal(0L, opened.AvailableAmount);   // opened empty — money only ever arrives via Credit
        Assert.Equal("VND", opened.Currency);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Once);
    }

    // Idempotent: a retried provision callback (or a backfill racing activation) finds the wallet and
    // does nothing — never a second row, never a reset balance, never a save.
    [Fact]
    public async Task OpenForTenant_WalletExists_IsNoOp()
    {
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync(Wallet.Open(Tenant, "VND"));

        await CreateService().OpenForTenantAsync(Tenant);

        _walletRepo.Verify(r => r.Add(It.IsAny<Wallet>()), Times.Never);
        _walletRepo.Verify(r => r.SaveChangesAsync(default), Times.Never);
    }

    // Two opens race: the loser's INSERT hits the UNIQUE(tenant_id) index (surfaced as
    // ConcurrencyConflictException). One wallet per tenant IS the goal, so losing the race is success.
    [Fact]
    public async Task OpenForTenant_ConcurrentOpen_SwallowsConflict()
    {
        _walletRepo.Setup(r => r.GetByTenantForUpdateAsync(Tenant, default)).ReturnsAsync((Wallet?)null);
        _walletRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConcurrencyConflictException("WALLET_CONCURRENCY_CONFLICT", "lost the open race"));

        await CreateService().OpenForTenantAsync(Tenant);   // does not throw
    }

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
        _walletRepo.Setup(r => r.ListTransactionsByTenantAsync(Tenant, default)).ReturnsAsync([
            At(WalletTransaction.ForPromo(wallet, 100L, Guid.NewGuid(), 100L), t0),
            At(WalletTransaction.ForTopUp(wallet, 900000L, Guid.NewGuid(), 900100L), t0.AddDays(1)),
            At(WalletTransaction.ForInvoicePayment(wallet, 40L, Guid.NewGuid(), 900060L), t0.AddDays(2)),
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
        _walletRepo.Setup(r => r.ListTransactionsByTenantAsync(Tenant, default)).ReturnsAsync([
            At(WalletTransaction.ForTopUp(wallet, 500L, Guid.NewGuid(), 500L), t0),
            At(WalletTransaction.ForInvoicePayment(wallet, 200L, Guid.NewGuid(), 300L), t0.AddDays(4)),
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
        _walletRepo.Setup(r => r.ListTransactionsByTenantAsync(Tenant, default)).ReturnsAsync([]);

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

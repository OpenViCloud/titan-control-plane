using System.Reflection;
using Moq;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Tests.Domain;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox and titan-ops features/billing/topup/design.md#promo-grant.
// Silent about two confirms at once: UNIQUE(ref_type, ref_id) answers those, TopUpConfirmDbTests runs them on Postgres.
public class TopUpCreditTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private readonly Mock<IWalletRepository> _wallets = new();
    private readonly Mock<IWalletMutationRepository> _mutations = new();
    private readonly Mock<ITopUpRepository> _topUps = new();
    private readonly Mock<IWalletTransactionRepository> _transactions = new();
    private readonly Wallet _wallet = Wallet.Open(TenantId, "VND");

    private TopUpCredit Credit() => new(_wallets.Object, _mutations.Object, _topUps.Object, new WalletLedger(_transactions.Object));

    private void HasAWallet() =>
        _wallets.Setup(w => w.GetByTenantForUpdateAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(_wallet);

    private static TopUp Pending() => TopUp.Create(TenantId, 100_000L, "VND");

    [Fact]
    public async Task Confirm_MakesItConfirmed_AndAddsOneCreditForItsAmount()
    {
        HasAWallet();
        TopUp topUp = Pending();

        WalletMutation credit = await Credit().ConfirmAsync(topUp, TopUpSource.Manual, "admin-1", "BANK-7", null);

        Assert.Equal(TopUpStatus.Confirmed, topUp.Status);
        Assert.Equal(WalletMutationType.Credit, credit.Type);
        Assert.Equal(WalletMutationStatus.Pending, credit.Status);
        Assert.Equal(topUp.Amount, credit.Amount);
        Assert.Equal(_wallet.Id, credit.WalletId);
        Assert.Equal(WalletMutation.TopUpRefType, credit.RefType);
        Assert.Equal(topUp.Id, credit.RefId);
        _mutations.Verify(m => m.Add(credit), Times.Once);
    }

    [Fact]
    public async Task Confirm_ATopUpThatFailed_IsRefused_AndAddsNoCredit()
    {
        HasAWallet();
        TopUp topUp = Pending();
        topUp.Fail("the bank said no");

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => Credit().ConfirmAsync(topUp, TopUpSource.Manual, "admin-1", null, null));

        Assert.Equal("TOPUP_INVALID_STATE", ex.Code);
        _mutations.Verify(m => m.Add(It.IsAny<WalletMutation>()), Times.Never);
    }

    [Fact]
    public async Task Confirm_ATenantWithoutAWallet_IsNotFound_AndAddsNoCredit()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Credit().ConfirmAsync(Pending(), TopUpSource.Manual, "admin-1", null, null));

        Assert.Equal("WALLET_NOT_FOUND", ex.Code);
        _mutations.Verify(m => m.Add(It.IsAny<WalletMutation>()), Times.Never);
    }

    [Fact]
    public async Task GrantPromo_AddsTheConfirmedTopUp_AndMovesTheMoneyAsPromo()
    {
        HasAWallet();
        WalletTransaction? row = null;
        _transactions.Setup(t => t.Add(It.IsAny<WalletTransaction>())).Callback<WalletTransaction>(t => row = t);

        TopUp topUp = await Credit().GrantPromoAsync(TenantId, 50_000L, "VND", "welcome", "admin-1");

        Assert.Equal(TopUpStatus.Confirmed, topUp.Status);
        Assert.Equal(TopUpSource.PromotionalCredit, topUp.Source);
        Assert.Equal(50_000L, _wallet.AvailableAmount);
        Assert.NotNull(row);
        Assert.Equal(WalletTransactionType.Promo, row.Type);
        Assert.Equal(topUp.Id, row.RefId);
        _topUps.Verify(r => r.AddTopUp(topUp), Times.Once);
    }

    [Fact]
    public async Task GrantPromo_ATenantWithoutAWallet_IsNotFound_AndAddsNothing()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Credit().GrantPromoAsync(TenantId, 50_000L, "VND", null, "admin-1"));

        Assert.Equal("WALLET_NOT_FOUND", ex.Code);
        _topUps.Verify(r => r.AddTopUp(It.IsAny<TopUp>()), Times.Never);
    }

    [Fact]
    public void OnlyTopUpCreditConfirmsOrGrantsATopUp()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] publicMethods = [.. typeof(TopUp).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(TopUp.Create), publicMethods);
        Assert.DoesNotContain(nameof(TopUp.Confirm), publicMethods);
        Assert.DoesNotContain(nameof(TopUp.GrantPromo), publicMethods);
    }
}

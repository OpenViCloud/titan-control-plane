using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// A promotional credit is a TopUp with Source=PromotionalCredit, born Confirmed.
public class PromotionalCreditTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void GrantPromo_IsBornConfirmed_WithPromoSource_AndAudit()
    {
        var topUp = TopUp.GrantPromo(Tenant, 500_000L, "VND", "Launch promo", "admin-1");

        Assert.Equal(Tenant, topUp.TenantId);
        Assert.Equal(500_000L, topUp.Amount);
        Assert.Equal("VND", topUp.Currency);
        Assert.Equal(TopUpStatus.Confirmed, topUp.Status);
        Assert.Equal(TopUpSource.PromotionalCredit, topUp.Source);
        Assert.Equal("Launch promo", topUp.Reason);
        Assert.Equal("admin-1", topUp.ConfirmedBy);
        Assert.NotNull(topUp.ConfirmedAt);
        Assert.NotEqual(default, topUp.CreatedAt);
        Assert.False(string.IsNullOrEmpty(topUp.PaymentRef)); // Titan-side movement id
    }

    [Fact]
    public void GrantPromo_BlankReason_StoredAsNull()
    {
        Assert.Null(TopUp.GrantPromo(Tenant, 500_000L, "VND", "   ", "admin-1").Reason);
        Assert.Null(TopUp.GrantPromo(Tenant, 500_000L, "VND", null, "admin-1").Reason);
    }

    [Fact]
    public void GrantPromo_NonPositiveAmount_Throws()
    {
        Assert.Throws<ArgumentException>(() => TopUp.GrantPromo(Tenant, 0L, "VND", null, "admin-1"));
        Assert.Throws<ArgumentException>(() => TopUp.GrantPromo(Tenant, -10L, "VND", null, "admin-1"));
    }

    // A promo TopUp is already Confirmed, so the lifecycle methods refuse it. This is what stops
    // the SePay webhook or an admin Confirm from ever re-crediting a giveaway.
    [Fact]
    public void GrantPromo_CannotBeConfirmedAgain()
    {
        var topUp = TopUp.GrantPromo(Tenant, 500_000L, "VND", null, "admin-1");
        var ex = Assert.Throws<BusinessException>(
            () => topUp.Confirm(TopUpSource.SePay, "webhook", "txn-1"));
        Assert.Equal("TOPUP_INVALID_STATE", ex.Code);
    }

    [Fact]
    public void GrantPromo_CannotFail()
    {
        var topUp = TopUp.GrantPromo(Tenant, 500_000L, "VND", null, "admin-1");
        var ex = Assert.Throws<BusinessException>(() => topUp.Fail("nope"));
        Assert.Equal("TOPUP_INVALID_STATE", ex.Code);
    }

    // A plain TopUp is NOT a promo: no Source until confirmed,
    // no reason, and it confirms with SePay/Manual. The two shapes never blur.
    [Fact]
    public void Create_IsRealMoney_NotPromo()
    {
        var topUp = TopUp.Create(Tenant, 500_000L, "VND");

        Assert.Equal(TopUpStatus.Pending, topUp.Status);
        Assert.Null(topUp.Source);
        Assert.Null(topUp.Reason);

        topUp.Confirm(TopUpSource.SePay, "webhook", "txn-1");
        Assert.Equal(TopUpSource.SePay, topUp.Source);
        Assert.NotEqual(TopUpSource.PromotionalCredit, topUp.Source);
    }
}

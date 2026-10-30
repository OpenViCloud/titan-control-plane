using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// A TopUp records a purchase of credit. It starts Pending with a generated PaymentRef (the reconciliation
// code), and moves to Confirmed once the money is in.
public class TopUpTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    [Fact]
    public void Create_StartsPending_WithGeneratedPaymentRef()
    {
        var t = TopUp.Create(Tenant, 100L, "SGD");

        Assert.Equal(TopUpStatus.Pending, t.Status);
        Assert.Equal(Tenant, t.TenantId);
        Assert.Equal(100L, t.Amount);
        Assert.Equal("SGD", t.Currency);
        Assert.StartsWith("TITAN-", t.PaymentRef);
        Assert.Null(t.Source);        // channel is decided at confirm time
        Assert.Null(t.ConfirmedAt);
        Assert.Null(t.PaymentTxnRef);
    }

    [Fact]
    public void Create_GeneratesDistinctPaymentRefs()
    {
        var a = TopUp.Create(Tenant, 10L, "SGD");
        var b = TopUp.Create(Tenant, 10L, "SGD");

        Assert.NotEqual(a.PaymentRef, b.PaymentRef);
    }

    [Fact]
    public void Create_NonPositiveAmount_Throws()
    {
        Assert.Throws<ArgumentException>(() => TopUp.Create(Tenant, 0L, "SGD"));
        Assert.Throws<ArgumentException>(() => TopUp.Create(Tenant, -5L, "SGD"));
    }

    [Fact]
    public void Confirm_FromPending_MovesToConfirmed_StampsChannelAndRefs()
    {
        var t = TopUp.Create(Tenant, 100L, "SGD");

        t.Confirm(TopUpSource.Manual, "admin-1", "BANK-TXN-42");

        Assert.Equal(TopUpStatus.Confirmed, t.Status);
        Assert.Equal(TopUpSource.Manual, t.Source);
        Assert.Equal("admin-1", t.ConfirmedBy);
        Assert.Equal("BANK-TXN-42", t.PaymentTxnRef);
        Assert.NotNull(t.ConfirmedAt);
    }

    // A confirmed TopUp cannot be confirmed again: the domain half of no-double-credit; the DB half is
    // WalletMutation's UNIQUE(ref_type, ref_id) (WalletMutationSchemaTests.UniquePerRefTypeAndRefId).
    [Fact]
    public void Confirm_AlreadyConfirmed_ThrowsInvalidState()
    {
        var t = TopUp.Create(Tenant, 100L, "SGD");
        t.Confirm(TopUpSource.SePay, "sys", "txn-1");

        var ex = Assert.Throws<BusinessException>(() => t.Confirm(TopUpSource.SePay, "sys", "txn-1"));
        Assert.Equal("TOPUP_INVALID_STATE", ex.Code);
    }

    [Fact]
    public void Fail_FromPending_MovesToFailed_WithReason()
    {
        var t = TopUp.Create(Tenant, 100L, "SGD");

        t.Fail("payment declined");

        Assert.Equal(TopUpStatus.Failed, t.Status);
        Assert.Equal("payment declined", t.FailureReason);
    }

    [Fact]
    public void Fail_AlreadyConfirmed_ThrowsInvalidState()
    {
        var t = TopUp.Create(Tenant, 100L, "SGD");
        t.Confirm(TopUpSource.Manual, "admin", "ref");

        var ex = Assert.Throws<BusinessException>(() => t.Fail("too late"));
        Assert.Equal("TOPUP_INVALID_STATE", ex.Code);
    }
}

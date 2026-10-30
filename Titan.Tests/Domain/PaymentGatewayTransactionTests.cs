using Titan.Domain.Aggregates.PaymentGatewayTransaction;

namespace Titan.Tests.Domain;

// The record of what a gateway reported as money in. Zero is kept (an orphan of nothing is still evidence an admin
// reconciles); a negative amount is not money in at all.
public class PaymentGatewayTransactionTests
{
    private static PaymentGatewayTransaction Record(long amount) =>
        PaymentGatewayTransaction.Record(
            "sepay-1", PaymentGatewayTransactionStatus.Orphan, amount, "MBBank",
            DateTimeOffset.UtcNow, rawContent: null, tenantId: null, topUpId: null);

    [Fact]
    public void Record_WithANegativeAmount_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Record(-1L));
    }

    [Fact]
    public void Record_WithZero_IsKept()
    {
        Assert.Equal(0L, Record(0L).Amount);
    }
}

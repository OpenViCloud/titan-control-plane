
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Billing.PaymentGatewayTransaction;

/// One row per SePay transaction id (the PK), so the DB dedupes a retried webhook, and every payment —
/// matched or not — leaves a durable trail for reconciliation (titan-ops architecture/money-and-wallet.md#sepay-writer).
public class PaymentGatewayTransaction : AggregateRoot
{
    /// SePay's own transaction id.
    public string Id { get; private set; } = default!;

    /// Null for an Orphan.
    public Guid? TopUpId { get; private set; }

    /// Null for an Orphan.
    public Guid? TenantId { get; private set; }

    /// What SePay reported transferred, kept even for a mismatch or orphan so reconciliation sees it.
    public long Amount { get; private set; }

    /// e.g. "Vietcombank" — audit only.
    public string Gateway { get; private set; } = default!;

    /// The raw transfer memo, for an admin reconciling an orphan by eye.
    public string? RawContent { get; private set; }

    public PaymentGatewayTransactionStatus Status { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    /// The payment's own time from SePay, not arrival: the TopUp is confirmed at this time.
    public DateTimeOffset TransactionDate { get; private set; }

    private PaymentGatewayTransaction() { }

    public static PaymentGatewayTransaction Record(
        string txnId, PaymentGatewayTransactionStatus status, long amount, string gateway, DateTimeOffset transactionDate,
        string? rawContent, Guid? tenantId, Guid? topUpId)
    {
        // Money in is never negative. Zero is kept: the row is the reconciliation evidence of what the gateway sent.
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A gateway payment cannot be negative.");
        }

        return new PaymentGatewayTransaction
        {
            Id = txnId,
            Status = status,
            Amount = amount,
            Gateway = gateway,
            TransactionDate = transactionDate,
            RawContent = rawContent,
            TenantId = tenantId,
            TopUpId = topUpId,
            ReceivedAt = DateTimeOffset.UtcNow,
        };
    }
}

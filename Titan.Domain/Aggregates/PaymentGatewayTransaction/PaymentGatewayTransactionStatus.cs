namespace Titan.Domain.Aggregates.PaymentGatewayTransaction;

/// Stored by member name (titan-ops architecture/code-structure.md#enum-storage), so reconciliation queries by
/// status stay legible.
public enum PaymentGatewayTransactionStatus
{
    /// Matched a pending TopUp by reconciliation code and amount — credited.
    Matched,

    /// The TopUp it matched was already Confirmed — a retried/duplicate webhook, no credit.
    Duplicate,

    /// No TopUp matched the reference — awaits an admin reconciling by eye.
    Orphan,

    /// Matched a TopUp but the transferred amount did not equal the expected amount — no credit.
    AmountMismatch
}

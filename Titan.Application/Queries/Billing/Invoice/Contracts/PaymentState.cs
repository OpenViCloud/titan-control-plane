namespace Titan.Application.Queries.Billing.Invoice.Contracts;

/// Where a wallet payment for this invoice has got to (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
/// Null means nobody has pressed pay. Named for what the customer sees, not for the outbox machinery.
/// Only the read side speaks it (titan-ops architecture/http-contract.md#commands-return-no-data).
public enum PaymentState
{
    /// Accepted, money not moved yet. Status is still New — this is what tells the two apart.
    Processing,
    /// The debit went through. Status will read SettledByWallet alongside it.
    Settled,
    /// The debit will not happen (the balance was spent before it was applied, or it was given up on).
    /// The invoice is still New and can be paid again (titan-ops features/billing/invoice/design.md#retry-after-failed-debit).
    Failed
}

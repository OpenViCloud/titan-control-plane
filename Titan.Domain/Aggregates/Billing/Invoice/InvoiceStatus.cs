namespace Titan.Domain.Aggregates.Billing.Invoice;

/// The only values written to `invoices.status`, a concurrency token
/// (titan-ops architecture/money-and-wallet.md#double-pay-guard). Overdue and Settled are display labels in
/// <see cref="InvoiceDisplayStatus"/>, so the compiler forbids storing them.
public enum InvoiceStatus
{
    New,
    Paid,

    /// Paid from the wallet by a human action (titan-ops architecture/money-and-wallet.md#human-confirmed-debit); terminal,
    /// never Overdue. Paid means money arrived outside Titan and an admin recorded it.
    SettledByWallet
}

namespace Titan.Domain.Aggregates.Billing.Wallet;

/// TopUp vs Promo exists for accounting, not spending: money the customer paid (deferred revenue) stays
/// separable from credit Titan gave away (titan-ops architecture/money-and-wallet.md#tax-legal-posture). Paying an
/// invoice never looks at it (titan-ops architecture/money-and-wallet.md#credit-is-money).
public enum WalletTransactionType
{
    /// A confirmed paid TopUp. RefId is the TopUpId.
    TopUp,

    /// Free credit an admin granted. RefId is the TopUpId of the grant.
    Promo,

    /// Money that left to pay one invoice, and only ever because a person pressed a button
    /// (titan-ops architecture/money-and-wallet.md#human-confirmed-debit). RefId is the InvoiceId.
    InvoicePayment
}

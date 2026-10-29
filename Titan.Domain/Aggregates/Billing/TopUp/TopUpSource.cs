namespace Titan.Domain.Aggregates.Billing.TopUp;

/// The real-money / free-money line for accounting: SePay and Manual are real payments (ledger type TopUp,
/// deferred revenue); PromotionalCredit is credit an admin granted with no money changing hands (ledger
/// type Promo). See titan-ops architecture/money-and-wallet.md#credit-is-money.
public enum TopUpSource
{
    SePay,
    Manual,
    PromotionalCredit
}

namespace Titan.Domain.Aggregates.Billing.WalletMutation;

/// The mutation's amount is always positive; this decides the sign, because Wallet.Credit and Wallet.Debit
/// both take a positive amount (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
public enum WalletMutationType
{
    Credit,
    Debit
}

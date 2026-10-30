using Titan.Domain.Aggregates.PaymentGatewayTransaction;

namespace Titan.Domain.Repositories;

/// Audit + idempotency store for inbound SePay payments, keyed by SePay txn id.
public interface IPaymentGatewayTransactionRepository
{
    /// The dedupe check that makes a retried webhook a no-op.
    Task<bool> ExistsAsync(string txnId, CancellationToken ct);

    /// False when a concurrent webhook already inserted the same txn id (PK violation): the caller treats
    /// it as already processed, so the wallet is credited once.
    Task<bool> TryAddAsync(PaymentGatewayTransaction txn, CancellationToken ct);

    /// Untracked.
    Task<PaymentGatewayTransaction?> GetAsync(string txnId, CancellationToken ct);
}

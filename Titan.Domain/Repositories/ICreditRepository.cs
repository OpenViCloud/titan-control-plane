using Titan.Domain.Aggregates.TopUp;

namespace Titan.Domain.Repositories;

public interface ICreditRepository
{
    /// Newest first; `source` null lists every TopUp. Ordered by ConfirmedAt then CreatedAt so a Pending TopUp
    /// (no ConfirmedAt yet) still sorts sensibly.
    Task<IReadOnlyList<TopUp>> ListTopUpsAsync(TopUpSource? source, CancellationToken ct);

    void AddTopUp(TopUp topUp);

    /// Untracked.
    Task<TopUp?> GetTopUpAsync(Guid id, CancellationToken ct);

    Task<TopUp?> GetTopUpForUpdateAsync(Guid id, CancellationToken ct);

    /// Untracked. PaymentRef is Titan's reconciliation code; the SePay webhook attributes a payment by it.
    Task<TopUp?> GetTopUpByPaymentRefAsync(string paymentRef, CancellationToken ct);

    /// Newest first, untracked.
    Task<IReadOnlyList<TopUp>> ListTopUpsByTenantAsync(Guid tenantId, CancellationToken ct);

    /// Shares the DbContext with IWalletRepository, so either SaveChanges flushes the whole unit.
    Task SaveChangesAsync(CancellationToken ct);
}

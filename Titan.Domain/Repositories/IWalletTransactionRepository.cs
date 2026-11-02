using Titan.Domain.Aggregates.WalletTransaction;

namespace Titan.Domain.Repositories;

/// The ledger (titan-ops architecture/money-and-wallet.md#signed-ledger). Rows only arrive; nothing updates or removes one.
/// Shares the DbContext with IWalletRepository, whose SaveChangesAsync commits the row with the balance.
public interface IWalletTransactionRepository
{
    void Add(WalletTransaction transaction);

    /// Oldest first, untracked.
    Task<IReadOnlyList<WalletTransaction>> ListByTenantAsync(Guid tenantId, CancellationToken ct);
}

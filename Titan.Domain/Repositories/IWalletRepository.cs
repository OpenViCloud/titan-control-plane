using Titan.Domain.Aggregates.Wallet;

namespace Titan.Domain.Repositories;

/// Money moves through WalletLedger (titan-ops architecture/money-and-wallet.md#wallet-invariant-in-aggregate);
/// the balance and its ledger row commit in one SaveChanges, and the wallet's version token makes a
/// concurrent writer lose (titan-ops architecture/money-and-wallet.md#version-token).
public interface IWalletRepository
{
    /// Tracked. Null when no wallet was opened for the tenant.
    Task<Wallet?> GetByTenantForUpdateAsync(Guid tenantId, CancellationToken ct);

    /// Tracked, its row locked until the next SaveChanges: two replicas applying two mutations of one wallet take
    /// turns instead of one losing on the version token. A wallet mutation names its wallet by id, not tenant
    /// (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
    Task<Wallet?> GetByIdForUpdateAsync(Guid walletId, CancellationToken ct);

    /// Untracked. 0, and no row created, for a tenant with no wallet.
    Task<long> GetBalanceAsync(Guid tenantId, CancellationToken ct);

    void Add(Wallet wallet);

    /// One SaveChanges, one transaction: it also commits whatever else the caller changed on the shared
    /// context (a confirmed TopUp, an outbox mutation). Throws ConcurrencyConflictException when the wallet's
    /// version moved or a racing writer won a unique guard.
    Task SaveChangesAsync(CancellationToken ct);
}

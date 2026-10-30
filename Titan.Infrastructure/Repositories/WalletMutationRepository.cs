using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Shares the scope's DbContext with WalletRepository, so a mutation and its wallet commit in one SaveChanges
/// (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
public class WalletMutationRepository(TitanDbContext db) : IWalletMutationRepository
{
    public Task<WalletMutation?> GetForUpdateAsync(Guid mutationId, CancellationToken ct) =>
        db.WalletMutations.FirstOrDefaultAsync(c => c.Id == mutationId, ct);

    public void Add(WalletMutation mutation) => db.WalletMutations.Add(mutation);

    /// Raw SQL because FOR UPDATE SKIP LOCKED has no LINQ spelling. It runs in autocommit, so it skips only a row
    /// whose apply is committing at that instant (titan-ops features/billing/wallet/design.md#wallet-debts).
    /// 'Pending' is a literal because raw SQL bypasses EF value converters; it matches the stored token
    /// (WalletMutationSchemaTests.Status_IsStoredAsString). InMemory cannot run it: WalletMutationSweeperDbTests.
    public async Task<IReadOnlyList<WalletMutation>> ListPendingOlderThanAsync(
        DateTimeOffset cutoff, int batchSize, CancellationToken ct)
    {
        var rows = await db.WalletMutations
            .FromSql(
                $"""
                 SELECT * FROM core.wallet_mutations
                 WHERE status = 'Pending' AND queued_at < {cutoff}
                 ORDER BY queued_at
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(ct);

        return rows;
    }

    public Task<WalletMutation?> GetForRefAsync(string refType, Guid refId, CancellationToken ct) =>
        db.WalletMutations
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.RefType == refType && c.RefId == refId, ct);

    public Task<WalletMutation?> GetForRefForUpdateAsync(string refType, Guid refId, CancellationToken ct) =>
        db.WalletMutations
            .SingleOrDefaultAsync(c => c.RefType == refType && c.RefId == refId, ct);

    /// Poison is `Failed AND attempts >= maxAttempts`: a wallet shortfall is also Failed but never bumps
    /// Attempts, and is a normal outcome. Counting Failed alone would bury the stuck mutation among them
    /// (WalletMutationHealthSnapshotDbTests.Poison_CountsAttemptsExhausted_NotBusinessFailures). A reopened
    /// row is Pending with its attempts back at zero, so it stops counting on its own.
    public async Task<WalletMutationBacklog> GetHealthSnapshotAsync(
        DateTimeOffset now, int maxAttempts, CancellationToken ct)
    {
        var poison = await db.WalletMutations
            .CountAsync(c => c.Status == WalletMutationStatus.Failed && c.Attempts >= maxAttempts, ct);

        // COUNT + MIN in the database: pulling the rows back would get slower exactly when the backlog is large.
        var pending = await db.WalletMutations
            .Where(c => c.Status == WalletMutationStatus.Pending)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Oldest = g.Min(c => (DateTimeOffset?)c.QueuedAt) })
            .SingleOrDefaultAsync(ct);

        // No group at all = no pending rows (GroupBy over an empty set yields nothing).
        if (pending is null) return new WalletMutationBacklog(poison, 0, null);

        var oldest = pending.Oldest is { } at ? now - at : (TimeSpan?)null;
        return new WalletMutationBacklog(poison, pending.Count, oldest);
    }

    public async Task<IReadOnlyDictionary<Guid, WalletMutation>> GetForRefsAsync(
        string refType, IReadOnlyCollection<Guid> refIds, CancellationToken ct)
    {
        if (refIds.Count == 0) return new Dictionary<Guid, WalletMutation>();

        var rows = await db.WalletMutations
            .Where(c => c.RefType == refType && refIds.Contains(c.RefId))
            .AsNoTracking()
            .ToListAsync(ct);

        // UNIQUE(ref_type, ref_id): one row per ref, so ToDictionary cannot meet a duplicate key.
        return rows.ToDictionary(c => c.RefId);
    }
}

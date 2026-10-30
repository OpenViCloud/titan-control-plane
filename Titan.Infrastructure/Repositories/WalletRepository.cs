using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Keeps EF concurrency mechanics here: Application sees a domain ConcurrencyConflictException, never
/// DbUpdateConcurrencyException.
public class WalletRepository(TitanDbContext db) : IWalletRepository
{
    public Task<Wallet?> GetByTenantForUpdateAsync(Guid tenantId, CancellationToken ct) =>
        db.Wallets.FirstOrDefaultAsync(w => w.TenantId == tenantId, ct);

    public Task<Wallet?> GetByIdForUpdateAsync(Guid walletId, CancellationToken ct) =>
        db.Wallets.FirstOrDefaultAsync(w => w.Id == walletId, ct);

    public async Task<long> GetBalanceAsync(Guid tenantId, CancellationToken ct) =>
        // default(long) = 0 for a tenant with no wallet: asking never creates a row.
        await db.Wallets.AsNoTracking()
            .Where(w => w.TenantId == tenantId)
            .Select(w => w.AvailableAmount)
            .FirstOrDefaultAsync(ct);

    public void Add(Wallet wallet) => db.Wallets.Add(wallet);

    public void AddTransaction(WalletTransaction transaction) => db.WalletTransactions.Add(transaction);

    public async Task<IReadOnlyList<WalletTransaction>> ListTransactionsByTenantAsync(
        Guid tenantId, CancellationToken ct) =>
        await db.WalletTransactions
            .AsNoTracking()
            .Where(t => db.Wallets.Any(w => w.Id == t.WalletId && w.TenantId == tenantId))
            .OrderBy(t => t.CreatedAt) // chronological — the statement reads top to bottom
            .ToListAsync(ct);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The wallet's version moved between load and save — a concurrent payer committed first.
            throw new ConcurrencyConflictException("WALLET_CONCURRENCY_CONFLICT",
                "The wallet changed concurrently; the operation must be retried.", ex);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A racing writer won a unique guard first: wallets(tenant_id), wallet_transactions(type, ref_id) or
            // wallet_mutations(ref_type, ref_id). The caller re-reads the winner. Detach the losing inserts so a
            // retry on this scoped context does not resubmit them.
            foreach (var entry in db.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;

            throw new ConcurrencyConflictException("WALLET_CONCURRENCY_CONFLICT",
                "A concurrent write won a unique guard (wallet open or duplicate movement); retry.", ex);
        }
    }
}

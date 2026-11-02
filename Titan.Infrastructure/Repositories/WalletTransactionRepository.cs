using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class WalletTransactionRepository(TitanDbContext db) : IWalletTransactionRepository
{
    public void Add(WalletTransaction transaction) => db.WalletTransactions.Add(transaction);

    public async Task<IReadOnlyList<WalletTransaction>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
        await db.WalletTransactions
            .AsNoTracking()
            .Where(t => db.Wallets.Any(w => w.Id == t.WalletId && w.TenantId == tenantId))
            .OrderBy(t => t.CreatedAt) // chronological — the statement reads top to bottom
            .ToListAsync(ct);
}

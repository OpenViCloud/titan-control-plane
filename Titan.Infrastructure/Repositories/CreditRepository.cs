using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class CreditRepository(TitanDbContext db) : ICreditRepository
{
    /// `source` null = every TopUp. Ordered by ConfirmedAt then CreatedAt, so a still-Pending TopUp
    /// (ConfirmedAt null) sorts by when it was started.
    public async Task<IReadOnlyList<TopUp>> ListTopUpsAsync(TopUpSource? source, CancellationToken ct) =>
        await db.TopUps
            .AsNoTracking()
            .Where(t => source == null || t.Source == source)
            .OrderByDescending(t => t.ConfirmedAt)
            .ThenByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    public void AddTopUp(TopUp topUp) => db.TopUps.Add(topUp);

    /// No-track: for reads and the post-confirm re-read (report what the DB stored).
    public Task<TopUp?> GetTopUpAsync(Guid id, CancellationToken ct) =>
        db.TopUps.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    /// Tracked: the confirm path mutates the result and one SaveChanges persists it with the wallet mutation.
    public Task<TopUp?> GetTopUpForUpdateAsync(Guid id, CancellationToken ct) =>
        db.TopUps.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<TopUp?> GetTopUpByPaymentRefAsync(string paymentRef, CancellationToken ct) =>
        db.TopUps.AsNoTracking().FirstOrDefaultAsync(t => t.PaymentRef == paymentRef, ct);

    public async Task<IReadOnlyList<TopUp>> ListTopUpsByTenantAsync(Guid tenantId, CancellationToken ct) =>
        await db.TopUps
            .AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}

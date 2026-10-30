using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ActivityEvent;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class ActivityEventRepository(TitanDbContext db) : IActivityEventRepository
{
    public void Add(ActivityEvent entry) => db.ActivityEvents.Add(entry);

    public Task SaveAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public Task<List<ActivityEvent>> ListByTenantAsync(Guid tenantId, int limit, CancellationToken ct = default) =>
        db.ActivityEvents
            .Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
}

using Titan.Domain.Aggregates.ActivityEvent;
using Titan.Domain.Aggregates.Tenant;

namespace Titan.Domain.Repositories;

public interface IActivityEventRepository
{
    /// Shares the DbContext with the other repositories, so a sibling repository's SaveAsync persists
    /// it atomically with that write.
    void Add(ActivityEvent entry);

    Task SaveAsync(CancellationToken ct = default);

    /// Newest-first, capped at limit.
    Task<List<ActivityEvent>> ListByTenantAsync(Guid tenantId, int limit, CancellationToken ct = default);
}

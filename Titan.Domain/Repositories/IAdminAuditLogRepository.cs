using Titan.Domain.Aggregates.AdminAuditLog;

namespace Titan.Domain.Repositories;

public interface IAdminAuditLogRepository
{
    /// Shares the DbContext with the other repositories, so a sibling repository's SaveAsync persists
    /// it atomically with that write.
    void Add(AdminAuditLog entry);

    Task SaveAsync(CancellationToken ct = default);

    /// Newest-first, capped at limit.
    Task<List<AdminAuditLog>> ListAsync(int limit, CancellationToken ct = default);
}

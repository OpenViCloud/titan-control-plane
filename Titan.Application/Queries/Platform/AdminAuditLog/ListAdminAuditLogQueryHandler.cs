using Titan.Application.Queries.Platform.AdminAuditLog.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Platform.AdminAuditLog;

// Read side of the admin cross-tenant audit trail. No
// tenant-scoping — the endpoint is platform-admin only and the trail is deliberately cross-tenant.
public class ListAdminAuditLogQueryHandler(IAdminAuditLogRepository repo)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public async Task<List<AdminAuditLogQueryResult>> HandleAsync(ListAdminAuditLogQuery query, CancellationToken ct = default)
    {
        var limit = query.Limit;

        var clamped = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var entries = await repo.ListAsync(clamped, ct);
        return entries.Select(AdminAuditLogQueryResult.From).ToList();
    }
}

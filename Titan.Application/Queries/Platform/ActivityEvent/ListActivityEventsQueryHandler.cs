using Titan.Application.Abstractions;
using Titan.Application.Queries.Platform.ActivityEvent.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Platform.ActivityEvent;

/// Tenant-scoped recent-activity feed for the customer Dashboard.
public class ListActivityEventsQueryHandler(IActivityEventRepository repo, ICurrentUser currentUser)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public async Task<List<ActivityEventQueryResult>> HandleAsync(ListActivityEventsQuery query, CancellationToken ct = default)
    {
        var limit = query.Limit;

        var tenantId = currentUser.RequireTenantId();
        // limit <= 0 (missing/invalid query param) falls back to the default; anything above the cap
        // is clamped so a caller cannot ask for an unbounded page.
        var clamped = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        var events = await repo.ListByTenantAsync(tenantId, clamped, ct);
        return events.Select(ActivityEventQueryResult.From).ToList();
    }
}

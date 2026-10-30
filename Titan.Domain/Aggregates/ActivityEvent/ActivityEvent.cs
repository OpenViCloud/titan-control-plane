
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.ActivityEvent;

// The customer's activity feed: the request a member made (actor = the member) and the outcome the provider
// delivered (actor = "system") — titan-ops architecture/intent-and-observation.md#request-and-outcome. Kept
// separate from AdminAuditLog, the cross-tenant admin compliance log.
public class ActivityEvent : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Action { get; private set; } = default!;              // e.g. "vm.created"
    public string ResourceType { get; private set; } = default!;        // e.g. "vm", "firewall_rule"
    public Guid ResourceId { get; private set; }
    public string ResourceName { get; private set; } = default!;        // snapshot at event time: still reads after the resource is deleted
    public string ActorKeycloakUserId { get; private set; } = default!; // stored, not returned by the tenant API
    public DateTimeOffset CreatedAt { get; private set; }

    private ActivityEvent() { }

    public static ActivityEvent Create(
        Guid tenantId, string action, string resourceType, Guid resourceId,
        string resourceName, string? actorKeycloakUserId) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            ResourceName = resourceName,
            // Never let a null identity drop the whole activity row.
            ActorKeycloakUserId = actorKeycloakUserId ?? "unknown",
            CreatedAt = DateTimeOffset.UtcNow
        };
}

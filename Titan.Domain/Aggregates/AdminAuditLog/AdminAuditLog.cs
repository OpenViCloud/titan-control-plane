
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.AdminAuditLog;

// Durable, append-only trace of admin writes (titan-ops architecture/tenancy-and-admin.md#admin-audit). Append-only
// by construction: no mutator, all setters private.
public class AdminAuditLog : AggregateRoot
{
    public Guid Id { get; private set; }
    public string Action { get; private set; } = default!;              // e.g. "vm.delete.cross_tenant"
    public string ActorKeycloakUserId { get; private set; } = default!;
    public string ResourceType { get; private set; } = default!;        // e.g. "vm"
    public Guid ResourceId { get; private set; }                        // when the resource is GUID-keyed
    public Guid OwnerTenantId { get; private set; }                     // Guid.Empty = platform-scope
    public DateTimeOffset CreatedAt { get; private set; }

    /// For string-keyed resources: catalog offerings use slugs ("s"/"m"/"standard"), so ResourceId alone cannot
    /// say which row an action touched. Null for GUID-keyed resources.
    public string? ResourceKey { get; private set; }

    /// e.g. "161.388 -> 400000.000000": without it the trail says a price changed but not to what.
    public string? Detail { get; private set; }

    private AdminAuditLog() { }

    /// No owning tenant (a price applies to every tenant), so OwnerTenantId is Guid.Empty. Readers must render
    /// that as "platform", not as a tenant whose id happens to be all zeros.
    public static AdminAuditLog CreatePlatform(
        string action, string? actorKeycloakUserId, string resourceType, string resourceKey, string detail) =>
        new()
        {
            Id = Guid.NewGuid(),
            Action = action,
            ActorKeycloakUserId = actorKeycloakUserId ?? "unknown",
            ResourceType = resourceType,
            ResourceId = Guid.Empty,
            OwnerTenantId = Guid.Empty,
            ResourceKey = resourceKey,
            Detail = detail,
            CreatedAt = DateTimeOffset.UtcNow
        };

    public static AdminAuditLog Create(
        string action, string? actorKeycloakUserId, string resourceType, Guid resourceId, Guid ownerTenantId) =>
        new()
        {
            Id = Guid.NewGuid(),
            Action = action,
            // Never let a null identity drop the whole audit row.
            ActorKeycloakUserId = actorKeycloakUserId ?? "unknown",
            ResourceType = resourceType,
            ResourceId = resourceId,
            OwnerTenantId = ownerTenantId,
            CreatedAt = DateTimeOffset.UtcNow
        };
}

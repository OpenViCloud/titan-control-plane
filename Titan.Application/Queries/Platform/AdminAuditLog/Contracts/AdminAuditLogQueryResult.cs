using AdminAuditLogAggregate = Titan.Domain.Aggregates.AdminAuditLog.AdminAuditLog;

namespace Titan.Application.Queries.Platform.AdminAuditLog.Contracts;

public record AdminAuditLogQueryResult(
    Guid Id,
    string Action,
    string Actor,
    string ResourceType,
    Guid ResourceId,
    Guid OwnerTenantId,
    DateTimeOffset CreatedAt,
    // String-keyed resources (catalog offering slugs) and what changed. Null for GUID-keyed resources and for
    // rows older than these columns; the UI renders that as "—", and OwnerTenantId = Guid.Empty renders as
    // "platform" rather than a tenant whose id happens to be all zeros.
    string? ResourceKey,
    string? Detail)
{
    public static AdminAuditLogQueryResult From(AdminAuditLogAggregate a) =>
        new(a.Id, a.Action, a.ActorKeycloakUserId, a.ResourceType, a.ResourceId, a.OwnerTenantId,
            a.CreatedAt, a.ResourceKey, a.Detail);
}

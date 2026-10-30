
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Tenant;

public class Member : ChildEntity
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string KeycloakUserId { get; private set; } = default!;
    public DateTimeOffset JoinedAt { get; private set; }

    // Navigation — EF Core only
    public Tenant Tenant { get; private set; } = default!;

    private Member() { }

    internal static Member Create(Guid tenantId, string keycloakUserId)
    {
        return new Member
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KeycloakUserId = keycloakUserId,
            JoinedAt = DateTimeOffset.UtcNow
        };
    }
}

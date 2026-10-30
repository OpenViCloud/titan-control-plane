using Titan.Domain.Aggregates.Tenant;

namespace Titan.Application.Queries.Platform.Tenant.Contracts;

public record TenantMemberQueryResult(Guid Id, string KeycloakUserId, DateTimeOffset JoinedAt)
{
    public static TenantMemberQueryResult From(Member m) => new(m.Id, m.KeycloakUserId, m.JoinedAt);
}

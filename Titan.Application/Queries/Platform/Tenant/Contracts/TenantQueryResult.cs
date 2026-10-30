using System.Text.Json.Serialization;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using Titan.Domain.Aggregates.Tenant;

namespace Titan.Application.Queries.Platform.Tenant.Contracts;

// DebtCeiling is a plain per-tenant number the admin reads at month-end close — no override/effective pair.
// No provider account: a tenant's Incus project is per cluster and created with its first resource there
// (titan-ops architecture/tenancy-and-admin.md#approval-is-activation).
public record TenantQueryResult(
    Guid Id, string Name, [property: JsonConverter(typeof(JsonStringEnumConverter))] TenantStatus Status, int MaxVms, int MaxNetworks, DateTimeOffset CreatedAt, decimal DebtCeiling,
    int MaxVolumes, int MaxPublicIpv6)
{
    public static TenantQueryResult From(TenantAggregate t) => new(
        t.Id, t.Name, t.Status, t.Quota.MaxVms, t.Quota.MaxNetworks, t.CreatedAt, t.DebtCeiling,
        t.Quota.MaxVolumes, t.Quota.MaxPublicIpv6);
}

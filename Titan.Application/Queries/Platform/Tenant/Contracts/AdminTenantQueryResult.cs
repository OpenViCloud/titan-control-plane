using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Queries.Platform.Tenant.Contracts;

// One row of the admin tenant list: everything the admin portal edits — all nine caps of TenantQuota and the debt
// ceiling. Its own record, not TenantQueryResult, because the customer's GET /v1/tenant is part of the public API
// (api/openapi-v1.json) and the admin list is not; a field added here must not change what customers are served.
public record AdminTenantQueryResult(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset CreatedAt,
    decimal DebtCeiling,
    int MaxVms,
    int MaxNetworks,
    int MaxVolumes,
    int MaxVmSnapshotsPerVm,
    int MaxVolumeSnapshotsPerVolume,
    int MaxFirewallRulesPerNetwork,
    int MaxPublicIpv6,
    int MaxSshKeys,
    int MaxLoadBalancers)
{
    public static AdminTenantQueryResult From(TenantAggregate t) => new(
        t.Id, t.Name, t.Status.ToString(), t.CreatedAt, t.DebtCeiling,
        t.Quota.MaxVms, t.Quota.MaxNetworks, t.Quota.MaxVolumes, t.Quota.MaxVmSnapshotsPerVm,
        t.Quota.MaxVolumeSnapshotsPerVolume, t.Quota.MaxFirewallRulesPerNetwork, t.Quota.MaxPublicIpv6,
        t.Quota.MaxSshKeys, t.Quota.MaxLoadBalancers);
}

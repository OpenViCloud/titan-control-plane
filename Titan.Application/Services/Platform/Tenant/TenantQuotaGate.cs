using Titan.Domain.Exceptions;
using ApiTokenAggregate = Titan.Domain.Aggregates.ApiToken.ApiToken;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Services.Platform.Tenant;

/// Quota is a policy at the door, not an invariant of what is being created: lowering a cap removes nothing, so a
/// tenant over its cap is a valid state (titan-ops architecture/code-structure.md#quota-gate). The handler counts, this
/// decides, before the handler creates anything.
public static class TenantQuotaGate
{
    /// count is what already exists: per tenant for most kinds, on one VM, volume or network for the three kinds whose
    /// cap is per parent. `>=`, so a cap of 0 refuses the first one (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none):
    /// reading 0 as "unlimited" would grant infinite resources at the moment an admin tried to cut them off.
    public static void EnsureHasRoom(TenantAggregate tenant, TenantQuotaKind kind, int count)
    {
        var quota = tenant.Quota;
        (int cap, string code, string message) = kind switch
        {
            TenantQuotaKind.Vm => (quota.MaxVms, "VM_QUOTA_EXCEEDED", "VM quota exceeded."),
            TenantQuotaKind.Network => (quota.MaxNetworks, "NETWORK_QUOTA_EXCEEDED", "Network quota exceeded."),
            TenantQuotaKind.Volume => (quota.MaxVolumes, "VOLUME_QUOTA_EXCEEDED", $"Volume quota exceeded (max {quota.MaxVolumes})."),
            TenantQuotaKind.VmSnapshot => (quota.MaxVmSnapshotsPerVm, "VM_SNAPSHOT_QUOTA_EXCEEDED",
                $"VM snapshot quota exceeded (max {quota.MaxVmSnapshotsPerVm})."),
            TenantQuotaKind.VolumeSnapshot => (quota.MaxVolumeSnapshotsPerVolume, "VOLUME_SNAPSHOT_QUOTA_EXCEEDED",
                $"Snapshot quota exceeded (max {quota.MaxVolumeSnapshotsPerVolume} per volume)."),
            TenantQuotaKind.FirewallRule => (quota.MaxFirewallRulesPerNetwork, "FIREWALL_QUOTA_EXCEEDED",
                $"Firewall rule quota of {quota.MaxFirewallRulesPerNetwork} per network has been reached."),
            TenantQuotaKind.SshKey => (quota.MaxSshKeys, "SSH_KEY_QUOTA_EXCEEDED", $"SSH key quota exceeded (max {quota.MaxSshKeys})."),
            TenantQuotaKind.LoadBalancer => (quota.MaxLoadBalancers, "LOAD_BALANCER_QUOTA_EXCEEDED",
                $"Load balancer quota exceeded (max {quota.MaxLoadBalancers})."),
            TenantQuotaKind.PublicIpv6 => (quota.MaxPublicIpv6, "IPV6_QUOTA_EXCEEDED", $"Public IPv6 quota exceeded (max {quota.MaxPublicIpv6})."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A quota kind this tenant has no cap for."),
        };
        if (count >= cap)
        {
            throw new BusinessException(code, message);
        }
    }

    /// The API token cap is one number for every tenant, not one of TenantQuota's.
    public static void EnsureApiTokenRoom(int tokenCount)
    {
        if (tokenCount >= ApiTokenAggregate.MaxPerTenant)
        {
            throw new BusinessException("API_TOKEN_QUOTA_EXCEEDED", $"API token limit reached (max {ApiTokenAggregate.MaxPerTenant}).");
        }
    }
}

using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Platform.Tenant;

// titan-ops architecture/tenancy-and-admin.md#quota-value-object. `{ get; private init; }`, not a positional record: a
// positional record hands out a public `with`, and `quota with { MaxVms = -1 }` would skip Create's
// validation (TenantQuotaTests.NoPropertySetter_IsPubliclyWritable).
public sealed record TenantQuota : ValueObject
{
    public int MaxVms { get; private init; }
    public int MaxNetworks { get; private init; }
    public int MaxVolumes { get; private init; }

    /// Per VM, not per tenant — a tenant with 10 VMs may hold 10 x this many snapshots.
    public int MaxVmSnapshotsPerVm { get; private init; }

    /// Per volume, not per tenant.
    public int MaxVolumeSnapshotsPerVolume { get; private init; }

    /// Per network, not per tenant.
    public int MaxFirewallRulesPerNetwork { get; private init; }

    public int MaxPublicIpv6 { get; private init; }

    /// Per tenant: SSH public keys saved to pick at VM create.
    public int MaxSshKeys { get; private init; }

    /// Per tenant: network load balancers (titan-ops features/load-balancer/design.md#lb-quota-and-billing).
    public int MaxLoadBalancers { get; private init; }

    private TenantQuota() { }

    // The migration backfilled existing tenants with exactly these
    // (titan-ops architecture/tenancy-and-admin.md#quota-migration-backfill).
    public const int DefaultMaxVms = 10;
    public const int DefaultMaxNetworks = 1;
    public const int DefaultMaxVolumes = 10;
    public const int DefaultMaxVmSnapshotsPerVm = 3;
    public const int DefaultMaxVolumeSnapshotsPerVolume = 3;
    public const int DefaultMaxFirewallRulesPerNetwork = 20;
    public const int DefaultMaxPublicIpv6 = 10;
    public const int DefaultMaxSshKeys = 20;
    public const int DefaultMaxLoadBalancers = 5;

    public static TenantQuota Default => Create(
        DefaultMaxVms, DefaultMaxNetworks, DefaultMaxVolumes, DefaultMaxVmSnapshotsPerVm,
        DefaultMaxVolumeSnapshotsPerVolume, DefaultMaxFirewallRulesPerNetwork, DefaultMaxPublicIpv6, DefaultMaxSshKeys,
        DefaultMaxLoadBalancers);

    public static TenantQuota Create(
        int maxVms,
        int maxNetworks,
        int maxVolumes,
        int maxVmSnapshotsPerVm,
        int maxVolumeSnapshotsPerVolume,
        int maxFirewallRulesPerNetwork,
        int maxPublicIpv6,
        int maxSshKeys,
        int maxLoadBalancers)
    {
        // Zero is a valid cap — it means "none allowed", not "unlimited". Only negative is impossible.
        Require(maxVms, nameof(maxVms));
        Require(maxNetworks, nameof(maxNetworks));
        Require(maxVolumes, nameof(maxVolumes));
        Require(maxVmSnapshotsPerVm, nameof(maxVmSnapshotsPerVm));
        Require(maxVolumeSnapshotsPerVolume, nameof(maxVolumeSnapshotsPerVolume));
        Require(maxFirewallRulesPerNetwork, nameof(maxFirewallRulesPerNetwork));
        Require(maxPublicIpv6, nameof(maxPublicIpv6));
        Require(maxSshKeys, nameof(maxSshKeys));
        Require(maxLoadBalancers, nameof(maxLoadBalancers));

        return new TenantQuota
        {
            MaxVms = maxVms,
            MaxNetworks = maxNetworks,
            MaxVolumes = maxVolumes,
            MaxVmSnapshotsPerVm = maxVmSnapshotsPerVm,
            MaxVolumeSnapshotsPerVolume = maxVolumeSnapshotsPerVolume,
            MaxFirewallRulesPerNetwork = maxFirewallRulesPerNetwork,
            MaxPublicIpv6 = maxPublicIpv6,
            MaxSshKeys = maxSshKeys,
            MaxLoadBalancers = maxLoadBalancers
        };
    }

    // Changes two caps and keeps the other seven: rebuilding the value with Create at a call site that knows only two
    // caps is how the rest silently become 0 (TenantQuotaTests.WithVmAndNetworkCaps_PreservesTheOtherCaps).
    public TenantQuota WithVmAndNetworkCaps(int maxVms, int maxNetworks)
    {
        Require(maxVms, nameof(maxVms));
        Require(maxNetworks, nameof(maxNetworks));
        return this with { MaxVms = maxVms, MaxNetworks = maxNetworks };
    }

    private static void Require(int value, string name)
    {
        if (value < 0)
            throw new BusinessException("INVALID_QUOTA", $"Quota value '{name}' must be non-negative.");
    }
}

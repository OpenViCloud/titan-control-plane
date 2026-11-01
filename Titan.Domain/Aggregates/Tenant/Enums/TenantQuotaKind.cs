namespace Titan.Domain.Aggregates.Tenant;

/// What a tenant's quota caps (titan-ops architecture/tenancy-and-admin.md#tenant-quota).
public enum TenantQuotaKind
{
    Vm,
    Network,
    Volume,
    VmSnapshot,
    VolumeSnapshot,
    FirewallRule,
    SshKey,
    LoadBalancer,
    PublicIpv6,
}

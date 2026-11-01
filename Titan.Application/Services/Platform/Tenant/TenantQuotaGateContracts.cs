namespace Titan.Application.Services.Platform.Tenant;

/// What the tenant's quota caps. A handler names the kind it is creating when it asks TenantQuotaGate.
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

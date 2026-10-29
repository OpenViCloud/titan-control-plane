namespace Titan.Application.Commands.Platform.Tenant.Contracts;

// The nine caps of TenantQuota (titan-ops architecture/tenancy-and-admin.md#tenant-quota). The two the endpoint always
// carried stay required; the other seven are optional, and an absent one keeps the tenant's current value, so an admin
// portal that still sends two caps keeps working while the new one rolls out.
public record UpdateTenantQuotaCommand(
    Guid TenantId,
    int MaxVms,
    int MaxNetworks,
    int? MaxVolumes,
    int? MaxVmSnapshotsPerVm,
    int? MaxVolumeSnapshotsPerVolume,
    int? MaxFirewallRulesPerNetwork,
    int? MaxPublicIpv6,
    int? MaxSshKeys,
    int? MaxLoadBalancers);

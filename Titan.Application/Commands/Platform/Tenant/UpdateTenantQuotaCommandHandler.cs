using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Platform.Tenant;

/// Platform-admin updates a tenant's resource caps. Returns nothing (titan-ops architecture/http-contract.md#commands-return-no-data): the admin UI
/// reads the new quota back from the tenant list. Negative values are rejected in the domain
/// (defense in depth behind the request validator).
public class UpdateTenantQuotaCommandHandler(ITenantRepository repo)
{
    public async Task HandleAsync(UpdateTenantQuotaCommand command, CancellationToken ct = default)
    {
        var tenant = await repo.FindByIdAsync(command.TenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        // Each absent cap is read from THIS tenant's quota, one by one: rebuilding the value from defaults (or from
        // zero) is how a cap the admin never touched silently changes.
        TenantQuota current = tenant.Quota;
        TenantQuota updated = TenantQuota.Create(
            command.MaxVms,
            command.MaxNetworks,
            KeepOrReplace(command.MaxVolumes, current.MaxVolumes),
            KeepOrReplace(command.MaxVmSnapshotsPerVm, current.MaxVmSnapshotsPerVm),
            KeepOrReplace(command.MaxVolumeSnapshotsPerVolume, current.MaxVolumeSnapshotsPerVolume),
            KeepOrReplace(command.MaxFirewallRulesPerNetwork, current.MaxFirewallRulesPerNetwork),
            KeepOrReplace(command.MaxPublicIpv6, current.MaxPublicIpv6),
            KeepOrReplace(command.MaxSshKeys, current.MaxSshKeys),
            KeepOrReplace(command.MaxLoadBalancers, current.MaxLoadBalancers));

        tenant.UpdateQuota(updated);
        await repo.SaveAsync(ct);
    }

    private static int KeepOrReplace(int? requested, int current)
    {
        if (requested.HasValue)
        {
            return requested.Value;
        }
        return current;
    }
}

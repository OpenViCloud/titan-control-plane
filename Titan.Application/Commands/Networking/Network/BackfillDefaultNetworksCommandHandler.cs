using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Repositories;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.Network;

/// Every Active tenant: MaxNetworks 1 becomes 2, DefaultNetwork gives the network it never had, and a default network
/// made before it opened TCP 80 and 443 gets those rules (titan-ops features/network/design.md#default-network). Its CronJob is suspended, run by hand; a second run gives nothing twice. One save per
/// tenant: a second GiveAsync on the same DbContext before a save can pick the route the first one handed out, still
/// tracked as free by that query, and HandTo throws.
public class BackfillDefaultNetworksCommandHandler(
    ITenantRepository tenants,
    INetworkRepository networks,
    IJobPassLock passLock,
    DefaultNetwork defaultNetwork,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ILogger<BackfillDefaultNetworksCommandHandler> logger)
{
    public const string Job = "default-network-backfill";

    public async Task HandleAsync(BackfillDefaultNetworksCommand command, CancellationToken ct = default)
    {
        List<Guid> activeTenantIds = (await tenants.ListAllAsync(ct))
            .Where(t => t.IsActive)
            .Select(t => t.Id)
            .ToList();

        foreach (Guid tenantId in activeTenantIds)
        {
            // Per tenant: each save below commits the lock's transaction (PostgresJobPassLock).
            await passLock.TakeAsync(Job, ct);

            TenantAggregate? tenant = await tenants.FindByIdForUpdateAsync(tenantId, ct);
            // Suspended since the list was read.
            if (tenant == null || !tenant.IsActive)
            {
                await tenants.SaveAsync(ct);
                continue;
            }

            if (tenant.Quota.MaxNetworks == 1)
            {
                tenant.UpdateQuota(tenant.Quota.WithVmAndNetworkCaps(tenant.Quota.MaxVms, TenantQuota.DefaultMaxNetworks));
            }
            await defaultNetwork.GiveIfNeverHadOneAsync(tenant, ct);
            await OpenWebPortsAsync(tenant, ct);
            // Also when nothing changed: it ends the lock's transaction.
            await tenants.SaveAsync(ct);
        }
    }

    // The tenant row is locked first, then each network: no other path locks a network and then its tenant.
    private async Task OpenWebPortsAsync(TenantAggregate tenant, CancellationToken ct)
    {
        List<Guid> defaultNetworkIds = (await networks.ListByTenantAsync(tenant.Id, ct))
            .Where(n => n.OpensSshWhenCreated && n.Status == NetworkStatus.Created)
            .Select(n => n.Id)
            .ToList();
        foreach (Guid networkId in defaultNetworkIds)
        {
            NetworkAggregate? network = await networks.FindByIdForUpdateAsync(networkId, ct);
            if (network == null || network.Status != NetworkStatus.Created)
            {
                continue;
            }
            await DefaultNetworkPorts.OpenAsync(network, tenant, DefaultNetworkPorts.AddedLater, commands, activity, logger, ct);
        }
    }
}

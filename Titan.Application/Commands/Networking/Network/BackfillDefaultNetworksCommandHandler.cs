using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.Network;

/// Every Active tenant: MaxNetworks 1 becomes 2, and DefaultNetwork gives the network it never had
/// (titan-ops features/network/design.md#default-network). No CronJob; a second run gives nothing twice. One save per
/// tenant: a second GiveAsync on the same DbContext before a save can pick the route the first one handed out, still
/// tracked as free by that query, and HandTo throws.
public class BackfillDefaultNetworksCommandHandler(
    ITenantRepository tenants,
    IJobPassLock passLock,
    DefaultNetwork defaultNetwork)
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
            // Also when nothing changed: it ends the lock's transaction.
            await tenants.SaveAsync(ct);
        }
    }
}

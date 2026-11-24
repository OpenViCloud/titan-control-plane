using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Services.Platform.Tenant;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Services.Networking.Network;

/// The network a tenant that never had one gets: "default", public IPv6, on the first cluster with IPv6
/// (titan-ops features/network/design.md#default-network). Shared by the two activation handlers and the backfill job,
/// so it is a service; it never saves, the caller's one save commits it.
public class DefaultNetwork(
    INetworkRepository networks,
    NetworkRoutes routes,
    ClusterOptions clusters,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ILogger<DefaultNetwork> logger)
{
    public const string Name = "default";

    /// False, with a warning, when the tenant cannot have it: activation and the backfill must not fail over it.
    public async Task<bool> GiveIfNeverHadOneAsync(TenantAggregate tenant, CancellationToken ct = default)
    {
        bool everHadOne = await networks.HasEverHadOneAsync(tenant.Id, ct);
        if (everHadOne)
        {
            return false;
        }

        ClusterConfig? cluster = clusters.Clusters.FirstOrDefault(c => c.HasPublicIpv6);
        if (cluster == null)
        {
            logger.LogWarning("Tenant {TenantId}: no cluster with public IPv6 configured; no default network given.", tenant.Id);
            return false;
        }

        // No row at all, so both counts are 0.
        try
        {
            TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.Network, 0);
            TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.PublicIpv6, 0);
        }
        catch (BusinessException e)
        {
            logger.LogWarning("Tenant {TenantId}: {Code}; no default network given.", tenant.Id, e.Code);
            return false;
        }

        NetworkAggregate network = NetworkAggregate.Create(
            tenant.Id, NetworkName.Parse(Name), cluster.Id, chosenCidr: null, wantsPublicIpv6: true,
            opensSshWhenCreated: true);

        string routerAddress;
        try
        {
            // Throws before touching the network (ClusterIpv6Route.NextToGive), and the network is not added yet.
            routerAddress = await routes.GiveAsync(network, cluster.Ipv6Block!, ct);
        }
        catch (BusinessException e)
        {
            logger.LogWarning("Tenant {TenantId}: {Code}; no default network given.", tenant.Id, e.Code);
            return false;
        }

        networks.Add(network);
        activity.StageOutcome(tenant.Id, "network.create_requested", "network", network.Id, network.Name.Value);
        await commands.EnqueueNetworkCreateAsync(network, network.Operation.PendingId!.Value,
            TenantAggregate.ProjectNameFor(tenant.Id), routerAddress, ct);
        return true;
    }
}

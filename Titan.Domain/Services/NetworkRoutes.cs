using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Repositories;

namespace Titan.Domain.Services;

/// The one way a network holds or lets go of one of its cluster's routes (titan-ops features/network/design.md#ipv6-public):
/// a live network holds its route while it holds its /64, and gets both at once. The rule spans the network and the route, so it is a domain service;
/// the methods of both that move them are internal, and the route commits in the network's SaveChanges.
public class NetworkRoutes(IClusterIpv6RouteRepository routes)
{
    /// Locks the cluster's free routes until the save commits: a second create waits here, then only sees the routes
    /// still free. Returns the router address, which is not kept: it goes out once, in network.create.
    public async Task<string> GiveAsync(Network network, string block56, CancellationToken ct = default)
    {
        IReadOnlyList<ClusterIpv6Route> free = await routes.LockFreeAsync(network.ClusterId, ct);
        ClusterIpv6Route route = ClusterIpv6Route.NextToGive(network.ClusterId, free);
        route.HandTo(network.Id);
        network.ReceivePublicIpv6(route.SubnetIn(block56));
        return route.RouterAddressIn(block56);
    }

    /// The create command gave up. A network that let go of its /64 gives the route back now, so its number waits at the
    /// back of the line. The held route is read without a lock: no create locks a held route.
    public async Task RecordCreateGaveUpAsync(Network network, string? reason, string? detail, CancellationToken ct = default)
    {
        bool letGoOfItsSubnet = network.MarkRequestFailed(reason, detail);
        if (!letGoOfItsSubnet)
        {
            return;
        }
        ClusterIpv6Route? route = await routes.FindHeldByAsync(network.Id, ct);
        if (route != null)
        {
            route.TakeBack(DateTimeOffset.UtcNow);
        }
    }

    /// The delete command confirmed Incus no longer has the network, nor its router: the route goes back. A repeat finds
    /// no route held, and a network without public IPv6 never held one.
    public async Task<NetworkDeletionOutcome> RecordDeletedAsync(Network network, CancellationToken ct = default)
    {
        NetworkDeletionOutcome outcome = network.MarkDeleted();
        ClusterIpv6Route? route = await routes.FindHeldByAsync(network.Id, ct);
        if (route != null)
        {
            route.TakeBack(DateTimeOffset.UtcNow);
        }
        return outcome;
    }
}

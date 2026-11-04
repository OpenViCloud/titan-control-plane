using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

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

    /// The report of the network's own command (titan-ops architecture/resource-lifecycle.md#callback-routes), ended here
    /// so the route follows it: a create that failed before Incus had the network let go of its /64, and a delete done
    /// means Incus no longer has the network nor its router; either way the route goes back, its number waiting at the
    /// back of the line. The held route is read without a lock: no create locks a held route. A repeated report
    /// ends nothing and touches no route, and a network without public IPv6 never held one.
    public async Task<NetworkOperationEnded?> RecordEndedAsync(Network network, Guid operationId, NetworkOperationEnd end,
        CancellationToken ct = default)
    {
        NetworkOperationEnded? ended = network.EndOperation(operationId, end);
        if (ended == null)
        {
            return null;
        }
        bool deleteDone = ended.Operation.Kind == NetworkOperationKind.Delete && end.Outcome == OperationOutcome.Done;
        if (ended.LetGoOfPublicIpv6 || deleteDone)
        {
            ClusterIpv6Route? route = await routes.FindHeldByAsync(network.Id, ct);
            if (route != null)
            {
                route.TakeBack(DateTimeOffset.UtcNow);
            }
        }
        return ended;
    }
}

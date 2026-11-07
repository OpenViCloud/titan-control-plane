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

    /// The report that the network's own command ended Done (titan-ops architecture/resource-lifecycle.md#callback-routes),
    /// ended here so the route follows it: a delete done means Incus no longer has the network nor its router, so the
    /// route goes back, its number waiting at the back of the line. The held route is read without a lock: no create
    /// locks a held route. A repeated report ends nothing and touches no route, and a network without public IPv6
    /// never held one.
    public async Task<NetworkOperationKind?> FinishOperationAsync(Network network, Guid operationId,
        string? externalId, NetworkStatus? status, string? cidr, string? gateway, CancellationToken ct = default)
    {
        // The operation the network waits on says which Done this is.
        NetworkOperationKind? waiting = network.Operation.Kind;
        bool ended;
        switch (waiting)
        {
            case NetworkOperationKind.Create:
                ended = network.CreateSucceeded(operationId, externalId, status, cidr, gateway);
                break;
            case NetworkOperationKind.Delete:
                ended = network.DeleteSucceeded(operationId);
                break;
            default:
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        if (waiting == NetworkOperationKind.Delete)
        {
            await TakeBackTheRouteOfAsync(network, ct);
        }
        return waiting;
    }

    /// The report that the network's own command gave up, ended here so the route follows it: a create that failed
    /// before Incus had the network lets go of its /64, and the route goes back as for a delete done.
    public async Task<NetworkOperationKind?> FailOperationAsync(Network network, Guid operationId,
        string? externalId, NetworkStatus? status, string? cidr, string? gateway, string? reason, string? detail,
        CancellationToken ct = default)
    {
        bool heldAPublicIpv6 = network.HasPublicIpv6;
        NetworkOperationKind? waiting = network.Operation.Kind;
        bool ended;
        switch (waiting)
        {
            case NetworkOperationKind.Create:
                ended = network.CreateFailed(operationId, externalId, status, cidr, gateway, reason, detail);
                break;
            case NetworkOperationKind.Delete:
                ended = network.DeleteFailed(operationId, status, reason, detail);
                break;
            default:
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        bool letGoOfItsPublicIpv6 = heldAPublicIpv6 && !network.HasPublicIpv6;
        if (letGoOfItsPublicIpv6)
        {
            await TakeBackTheRouteOfAsync(network, ct);
        }
        return waiting;
    }

    private async Task TakeBackTheRouteOfAsync(Network network, CancellationToken ct)
    {
        ClusterIpv6Route? route = await routes.FindHeldByAsync(network.Id, ct);
        if (route != null)
        {
            route.TakeBack(DateTimeOffset.UtcNow);
        }
    }
}

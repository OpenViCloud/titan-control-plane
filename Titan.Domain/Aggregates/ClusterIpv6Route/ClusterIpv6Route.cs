using System.Net;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.ClusterIpv6Route;

/// One of the 255 static OVH routes of a cluster's /56 (titan-ops infra/incus/design.md#ipv6-address-plan), and the
/// network it belongs to now. Every route exists before any network; a free route has no network.
public class ClusterIpv6Route : IAggregateRoot
{
    public const int FirstNumber = 1;
    public const int LastNumber = 255;

    public string ClusterId { get; private set; } = default!;
    public int Number { get; private set; }
    public Guid? NetworkId { get; private set; }
    /// When the route last went back; null while held, for a route never used, and for one that went back before the time
    /// was recorded. Only orders the free routes.
    public DateTimeOffset? ReleasedAt { get; private set; }

    public bool IsFree => NetworkId == null;

    private ClusterIpv6Route() { }

    /// A free route, for a test. The rows themselves come from the repository, in one statement per cluster.
    internal static ClusterIpv6Route Create(string clusterId, int number)
    {
        if (number < FirstNumber || number > LastNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "The first /64 holds OVH's gateway and the routers.");
        }
        return new ClusterIpv6Route { ClusterId = clusterId, Number = number };
    }

    /// The route a new network gets, out of the free routes of its cluster. A router re-created on the address of one
    /// deleted minutes before gets no packet for minutes (titan-ops features/network/design.md#ipv6-public), so a route
    /// with no release on record comes first (the lowest number), then the one given back longest ago.
    public static ClusterIpv6Route NextToGive(string clusterId, IReadOnlyCollection<ClusterIpv6Route> free)
    {
        // Null sorts first: no release on record.
        ClusterIpv6Route? next = free.OrderBy(r => r.ReleasedAt).ThenBy(r => r.Number).FirstOrDefault();
        if (next == null)
        {
            throw new BusinessException("IPV6_NOT_AVAILABLE", $"Cluster '{clusterId}' has no free public IPv6 /64 left.");
        }
        return next;
    }

    /// Internal: NetworkRoutes is the only way in, so the network gets the route's /64 with it.
    internal void HandTo(Guid networkId)
    {
        if (!IsFree)
        {
            throw new InvalidOperationException($"Route {Number} of cluster '{ClusterId}' already belongs to network {NetworkId}.");
        }
        NetworkId = networkId;
        ReleasedAt = null;
    }

    /// Internal: NetworkRoutes is the only way in, so the route goes back exactly when its network lets go.
    internal void TakeBack(DateTimeOffset at)
    {
        if (IsFree)
        {
            throw new InvalidOperationException($"Route {Number} of cluster '{ClusterId}' belongs to no network.");
        }
        NetworkId = null;
        ReleasedAt = at;
    }

    /// Route number n is the n-th /64 of the /56.
    public string SubnetIn(string block56)
    {
        byte[] subnet = IPNetwork.Parse(block56).BaseAddress.GetAddressBytes();
        subnet[7] = (byte)Number;
        return new IPAddress(subnet) + "/64";
    }

    /// ::(0x100 + n) of the first /64, the next hop of the static OVH route.
    public string RouterAddressIn(string block56)
    {
        byte[] router = IPNetwork.Parse(block56).BaseAddress.GetAddressBytes();
        router[14] = 0x01;
        router[15] = (byte)Number;
        return new IPAddress(router).ToString();
    }
}

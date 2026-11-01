using System.Net;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.ClusterIpv6Pool;

/// The 255 /64 blocks of a cluster's /56: which network holds each and when each was given back, across every tenant
/// (titan-ops infra/incus/design.md#ipv6-address-plan). One pool per cluster; its Version is the guard between two
/// requests that load it at once (titan-ops features/network/design.md#ipv6-public).
public class ClusterIpv6Pool : AggregateRoot
{
    public const int FirstNumber = 1;
    public const int LastNumber = 255;

    public Guid Id { get; private set; }
    public string ClusterId { get; private set; } = default!;
    public int Version { get; private set; }

    private readonly List<Ipv6Block64Holding> _holdings = [];
    public IReadOnlyCollection<Ipv6Block64Holding> Holdings => _holdings;

    private ClusterIpv6Pool() { }

    public static ClusterIpv6Pool Create(string clusterId)
    {
        if (string.IsNullOrWhiteSpace(clusterId))
        {
            throw new ArgumentException("clusterId must not be empty", nameof(clusterId));
        }
        return new ClusterIpv6Pool { Id = Guid.NewGuid(), ClusterId = clusterId };
    }

    /// Test seam — internal, reachable only from Titan.Tests. A held block gets a network id of its own.
    internal static ClusterIpv6Pool CreateUnchecked(string clusterId, params (int Number, bool Held, DateTimeOffset? GivenBackAt)[] holdings)
    {
        ClusterIpv6Pool pool = Create(clusterId);
        foreach (var holding in holdings)
        {
            EnsureNumberInRange(holding.Number);
            if (holding.Held)
            {
                pool._holdings.Add(Ipv6Block64Holding.Held(pool.Id, holding.Number, Guid.NewGuid()));
            }
            else
            {
                pool._holdings.Add(Ipv6Block64Holding.GivenBack(pool.Id, holding.Number, holding.GivenBackAt));
            }
        }
        return pool;
    }

    public bool IsBlock64HeldBy(Guid networkId)
    {
        return _holdings.Any(h => h.HolderNetworkId == networkId);
    }

    /// The /64 block a network of the cluster gets, out of the cluster's /56 (block56); IPV6_NOT_AVAILABLE when all 255
    /// are held. A router re-created on the address of one deleted minutes before gets no packet for minutes
    /// (titan-ops features/network/design.md#ipv6-public), so a block never held comes first (the lowest number), then
    /// the one given back longest ago; a block given back with no time recorded counts as the oldest.
    public GrantedBlock64 GiveBlock64To(Guid networkId, string block56)
    {
        if (IsBlock64HeldBy(networkId))
        {
            throw new InvalidOperationException($"Network {networkId} already holds a /64 block of cluster '{ClusterId}'.");
        }
        int? number = NextNumberToGive();
        if (number == null)
        {
            throw new BusinessException("IPV6_NOT_AVAILABLE", $"Cluster '{ClusterId}' has no free public IPv6 /64 left.");
        }
        GrantedBlock64 granted = GrantFor(block56, number.Value);
        Ipv6Block64Holding? existing = _holdings.SingleOrDefault(h => h.Number == number.Value);
        if (existing == null)
        {
            _holdings.Add(Ipv6Block64Holding.Held(Id, number.Value, networkId));
        }
        else
        {
            existing.HandTo(networkId);
        }
        Version++;
        return granted;
    }

    public void TakeBlock64BackFrom(Guid networkId)
    {
        TakeBlock64BackFrom(networkId, DateTimeOffset.UtcNow);
    }

    internal void TakeBlock64BackFrom(Guid networkId, DateTimeOffset at)
    {
        Ipv6Block64Holding? held = _holdings.SingleOrDefault(h => h.HolderNetworkId == networkId);
        if (held == null)
        {
            throw new InvalidOperationException($"Network {networkId} holds no /64 block of cluster '{ClusterId}'.");
        }
        held.TakeBack(at);
        Version++;
    }

    private int? NextNumberToGive()
    {
        int? oldestGivenBack = null;
        DateTimeOffset oldestGivenBackAt = DateTimeOffset.MaxValue;
        for (int number = FirstNumber; number <= LastNumber; number++)
        {
            Ipv6Block64Holding? holding = _holdings.SingleOrDefault(h => h.Number == number);
            if (holding == null)
            {
                return number;
            }
            if (holding.IsHeld)
            {
                continue;
            }
            DateTimeOffset when = holding.GivenBackAt ?? DateTimeOffset.MinValue;
            if (oldestGivenBack == null || when < oldestGivenBackAt)
            {
                oldestGivenBack = number;
                oldestGivenBackAt = when;
            }
        }
        return oldestGivenBack;
    }

    /// /64 number n is the n-th /64 of the /56; the router is ::(0x100 + n) of the first /64, the next hop of the
    /// static OVH route for that /64.
    private static GrantedBlock64 GrantFor(string block56, int number)
    {
        EnsureNumberInRange(number);
        byte[] subnet = IPNetwork.Parse(block56).BaseAddress.GetAddressBytes();
        byte[] router = (byte[])subnet.Clone();
        subnet[7] = (byte)number;
        router[14] = 0x01;
        router[15] = (byte)number;
        return new GrantedBlock64(number, new IPAddress(subnet) + "/64", new IPAddress(router).ToString());
    }

    private static void EnsureNumberInRange(int number)
    {
        if (number < FirstNumber || number > LastNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "The first /64 holds OVH's gateway and the routers.");
        }
    }
}

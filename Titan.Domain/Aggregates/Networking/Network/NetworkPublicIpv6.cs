using System.Net;

namespace Titan.Domain.Aggregates.Networking.Network;

/// A network's public IPv6 (titan-ops infra/incus/design.md#ipv6-address-plan): /64 number Index of the cluster's /56 and
/// the router address ::(0x100 + Index) of the first /64, the next hop of the static OVH route for that /64.
public sealed record NetworkPublicIpv6
{
    public const int FirstIndex = 1;
    public const int LastIndex = 255;

    public int Index { get; private init; }
    public string Subnet { get; private init; } = default!;
    public string RouterAddress { get; private init; } = default!;

    private NetworkPublicIpv6() { }

    /// The number a new network of the cluster gets, or null when all 255 are taken. A router re-created on the address
    /// of one deleted minutes before gets no packet for minutes (titan-ops features/network/design.md#ipv6-public), so a
    /// number never held comes first (the lowest), then the one given back longest ago. released maps each number a
    /// deleted network held to when it was given back; null is a row deleted before that was recorded: the oldest.
    public static NetworkPublicIpv6? FirstFree(string block, IReadOnlySet<int> taken,
        IReadOnlyDictionary<int, DateTimeOffset?> released)
    {
        int? oldestReleased = null;
        DateTimeOffset oldestReleasedAt = DateTimeOffset.MaxValue;
        for (int index = FirstIndex; index <= LastIndex; index++)
        {
            if (taken.Contains(index))
            {
                continue;
            }
            bool neverHeld = !released.TryGetValue(index, out DateTimeOffset? releasedAt);
            if (neverHeld)
            {
                return For(block, index);
            }
            DateTimeOffset when = releasedAt ?? DateTimeOffset.MinValue;
            if (oldestReleased == null || when < oldestReleasedAt)
            {
                oldestReleased = index;
                oldestReleasedAt = when;
            }
        }
        if (oldestReleased == null)
        {
            return null;
        }
        return For(block, oldestReleased.Value);
    }

    public static NetworkPublicIpv6 For(string block, int index)
    {
        if (index < FirstIndex || index > LastIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The first /64 holds OVH's gateway and the routers.");
        }
        byte[] subnet = IPNetwork.Parse(block).BaseAddress.GetAddressBytes();
        byte[] router = (byte[])subnet.Clone();
        subnet[7] = (byte)index;
        router[14] = 0x01;
        router[15] = (byte)index;
        return new NetworkPublicIpv6
        {
            Index = index,
            Subnet = new IPAddress(subnet) + "/64",
            RouterAddress = new IPAddress(router).ToString(),
        };
    }
}

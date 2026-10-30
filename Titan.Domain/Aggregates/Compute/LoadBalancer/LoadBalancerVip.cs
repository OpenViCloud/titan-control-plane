using System.Net;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Compute.LoadBalancer;

/// VIP number n of a network's /64 is interface id 0:0:1:n (titan-ops features/load-balancer/design.md#vip-in-network-64):
/// never the gateway ::1, never an EUI-64 address (those carry ff:fe mid-way). Incus does not refuse a VIP that
/// collides with an address in use, so Titan picks it.
public sealed record LoadBalancerVip : ValueObject
{
    public const int FirstIndex = 1;
    public const int LastIndex = 65535;

    public int Index { get; private init; }
    public string Address { get; private init; } = default!;

    private LoadBalancerVip() { }

    public static LoadBalancerVip For(string subnet, int index)
    {
        if (index < FirstIndex || index > LastIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "A VIP number is 1 to 65535.");
        }
        byte[] bytes = IPNetwork.Parse(subnet).BaseAddress.GetAddressBytes();
        for (int i = 8; i < 16; i++)
        {
            bytes[i] = 0;
        }
        bytes[13] = 0x01;
        bytes[14] = (byte)(index >> 8);
        bytes[15] = (byte)(index & 0xff);
        return new LoadBalancerVip { Index = index, Address = new IPAddress(bytes).ToString() };
    }

    /// The lowest number no live load balancer of the network holds, or null when every one is taken.
    public static LoadBalancerVip? FirstFree(string subnet, IReadOnlySet<int> taken)
    {
        for (int index = FirstIndex; index <= LastIndex; index++)
        {
            if (!taken.Contains(index))
            {
                return For(subnet, index);
            }
        }
        return null;
    }
}

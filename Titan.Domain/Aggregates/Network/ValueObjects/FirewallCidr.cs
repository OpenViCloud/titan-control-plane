using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Network;

/// The range a firewall rule matches: an IPv4 CIDR (Ipv4Cidr), or an IPv6 CIDR written as the range's first address
/// and not ::ffff:a.b.c.d — an IPv4 range in IPv6 clothing whose ICMP family the worker would have to guess
/// (titan-ops features/network/design.md#firewall-on-incus). The rule lives here so no path builds a rule Incus
/// refuses (titan-ops architecture/code-structure.md#logic-placement); also runs when EF materialises a row.
public sealed record FirewallCidr : ValueObject
{
    public string Value { get; private init; } = default!;

    private FirewallCidr() { }

    public static FirewallCidr Parse(string? value)
    {
        if (value == null || (!Ipv4Cidr.IsValid(value) && !IsIpv6Cidr(value)))
        {
            throw new BusinessException("INVALID_CIDR", "A firewall CIDR is an IPv4 CIDR like 0.0.0.0/0 or an IPv6 CIDR like ::/0.");
        }
        return new FirewallCidr { Value = value };
    }

    private static bool IsIpv6Cidr(string value)
    {
        // IPNetwork.TryParse refuses an address with host bits set, so 2001:db8::1/32 is not a range.
        if (!IPNetwork.TryParse(value, out IPNetwork network))
        {
            return false;
        }
        IPAddress address = network.BaseAddress;
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6;
    }

    public override string ToString() => Value;
}

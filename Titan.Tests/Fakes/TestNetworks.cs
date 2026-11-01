using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;

namespace Titan.Tests.Fakes;

/// A network as the create handler leaves it when public IPv6 was asked for: its create waits, and it has the /64 of
/// route 5 of the /56 2001:41d0:d00:d400::/56.
public static class TestNetworks
{
    private const string Subnet = "2001:41d0:d00:d405::/64";

    public static Network CreatingWithPublicIpv6(Tenant tenant, string name, string cluster, Ipv4Cidr? cidr = null)
    {
        Network network = Network.Create(tenant.Id, NetworkName.Parse(name), cluster, cidr, wantsPublicIpv6: true);
        network.ReceivePublicIpv6(Subnet);
        return network;
    }
}

using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// A network's public /64 (titan-ops infra/incus/design.md#ipv6-address-plan): asked for at create, given with the route
// the cluster has free (NetworkRoutesTests, ClusterIpv6RouteTests) and let go of when the create gives up before Incus
// had the network. The network keeps the subnet; the route table keeps who holds which.
public class NetworkPublicIpv6Tests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";

    private static Network WantingPublicIpv6(string cluster = "singapore01") =>
        Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), cluster, null, wantsPublicIpv6: true);

    private static Network HoldingASubnet()
    {
        Network network = WantingPublicIpv6();
        network.ReceivePublicIpv6(Subnet);
        return network;
    }

    private static Network Existing(Network network)
    {
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");
        network.MarkObserved(NetworkStatus.Created);
        return network;
    }

    [Fact]
    public void Create_WantingPublicIpv6_HasNoSubnetUntilARouteIsGiven()
    {
        Network network = WantingPublicIpv6();

        Assert.False(network.HasPublicIpv6);
        Assert.Null(network.PublicIpv6Subnet);

        network.ReceivePublicIpv6(Subnet);

        Assert.True(network.HasPublicIpv6);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
    }

    [Fact]
    public void Create_WithoutPublicIpv6_HasNone()
    {
        var network = Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "mock", null, wantsPublicIpv6: false);

        Assert.False(network.HasPublicIpv6);
        Assert.Null(network.PublicIpv6Subnet);
    }

    [Fact]
    public void ReceivePublicIpv6_Twice_Throws()
    {
        Network network = HoldingASubnet();

        Assert.Throws<InvalidOperationException>(() => network.ReceivePublicIpv6("2001:41d0:d00:d406::/64"));
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
    }

    [Fact]
    public void ReceivePublicIpv6_AfterTheCreateEnded_Throws()
    {
        Network network = WantingPublicIpv6();
        network.MarkRequestFailed("Network creation failed.", null);

        Assert.Throws<InvalidOperationException>(() => network.ReceivePublicIpv6(Subnet));
    }

    // The uplink of a network with public IPv6 routes only the private ranges.
    [Fact]
    public void Create_WantingPublicIpv6_WithACidrOutsideThePrivateRanges_Refuses()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "singapore01", Ipv4Cidr.Parse("8.8.8.0/24"), true));

        Assert.Equal("NETWORK_CIDR_NOT_PRIVATE", ex.Code);
    }

    [Fact]
    public void CreateGivesUpBeforeIncusHadIt_LetsGoOfTheSubnet_AndSaysSo()
    {
        Network network = HoldingASubnet();

        bool letGo = network.MarkRequestFailed("Network creation failed.", "detail");

        Assert.True(letGo);
        Assert.False(network.HasPublicIpv6);
        Assert.Null(network.PublicIpv6Subnet);
    }

    // Incus has the network, router and all: the subnet stays until the delete is confirmed.
    [Fact]
    public void CreateGivesUpAfterIncusHadIt_KeepsTheSubnet_AndSaysNothingWasLetGo()
    {
        Network network = HoldingASubnet();
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");

        bool letGo = network.MarkRequestFailed("Network creation failed.", "detail");

        Assert.False(letGo);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
    }

    [Fact]
    public void CreateGivesUp_OfANetworkWithoutASubnet_SaysNothingWasLetGo()
    {
        var network = Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "mock", null, wantsPublicIpv6: false);

        Assert.False(network.MarkRequestFailed("Network creation failed.", "detail"));
    }

    // A give-up with no create waiting is stale and records nothing: the route it might hold is not its to let go of.
    [Fact]
    public void ARepeatedGiveUp_SaysNothingWasLetGo()
    {
        Network network = HoldingASubnet();
        network.MarkRequestFailed("Network creation failed.", "detail");

        Assert.False(network.MarkRequestFailed("Network creation failed.", "detail"));
    }

    [Fact]
    public void DeleteConfirmed_KeepsTheSubnet()
    {
        Network network = Existing(HoldingASubnet());
        network.Delete();

        network.MarkDeleted();

        Assert.Equal(Subnet, network.PublicIpv6Subnet);
    }

    // The only other way a network row ends is a local delete of a failed create that has no provider id; a give-up
    // before Incus had the network already let the subnet go (the first assertion), so the local delete has none to keep.
    [Fact]
    public void RequestDelete_OfACreateThatGaveUpBeforeIncus_EndsDeleted_WithNoSubnetLeftToKeep()
    {
        Network network = HoldingASubnet();
        Assert.True(network.MarkRequestFailed("Network creation failed.", null));

        Assert.Null(network.Delete());

        Assert.Equal(NetworkStatus.Deleted, network.Status);
        Assert.False(network.HasPublicIpv6);
    }
}

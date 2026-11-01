using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Domain;

// A network's public /64 block (titan-ops infra/incus/design.md#ipv6-address-plan): asked for at create, counted against
// MaxPublicIpv6 by the factory, handed over by the cluster's pool (ClusterIpv6PoolTests) and let go of when the create
// gives up before Incus had the network or when the delete is confirmed. The network keeps the two addresses; the
// pool keeps the number, so each call that lets the block go says so and the caller takes it back in the pool.
public class NetworkBlock64Tests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";
    private const string Router = "2001:41d0:d00:d400::105";

    private static Tenant TenantWith(int maxNetworks = 9, int maxIpv6 = 9)
    {
        var t = TestTenant.Active("acme", "kc-1", "a@acme.com");
        t.UpdateQuota(TenantQuota.Create(9, maxNetworks, 9, 9, 9, 9, maxIpv6, 9, 9));
        return t;
    }

    private static Network WantingBlock64(string cluster = "singapore01") =>
        Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), cluster, null, wantsPublicIpv6: true);

    private static Network HoldingBlock64()
    {
        Network network = WantingBlock64();
        network.ReceivePublicIpv6(Subnet, Router);
        return network;
    }

    private static Network Existing(Network network)
    {
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");
        network.MarkObserved(NetworkStatus.Created);
        return network;
    }

    [Fact]
    public void Create_WantingPublicIpv6_HasNoBlockUntilThePoolHandsOneOver()
    {
        Network network = WantingBlock64();

        Assert.False(network.HasPublicIpv6);
        Assert.Null(network.PublicIpv6Subnet);

        network.ReceivePublicIpv6(Subnet, Router);

        Assert.True(network.HasPublicIpv6);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
        Assert.Equal(Router, network.PublicIpv6RouterAddress);
    }

    [Fact]
    public void Create_WithoutPublicIpv6_HasNone()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "mock", null, wantsPublicIpv6: false);

        Assert.False(network.HasPublicIpv6);
        Assert.Null(network.PublicIpv6Subnet);
        Assert.Null(network.PublicIpv6RouterAddress);
    }

    [Fact]
    public void ReceivePublicIpv6_Twice_Throws()
    {
        Network network = HoldingBlock64();

        Assert.Throws<InvalidOperationException>(() => network.ReceivePublicIpv6("2001:41d0:d00:d406::/64", "2001:41d0:d00:d400::106"));
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
    }

    [Fact]
    public void ReceivePublicIpv6_AfterTheCreateEnded_Throws()
    {
        Network network = WantingBlock64();
        network.MarkRequestFailed("Network creation failed.", null);

        Assert.Throws<InvalidOperationException>(() => network.ReceivePublicIpv6(Subnet, Router));
    }

    // The cap pair (QuotaInFactoryTests): at the cap the factory refuses, and only when IPv6 is asked for.
    [Fact]
    public void Create_AtTheIpv6Cap_Refuses_OnlyWhenIpv6IsAsked()
    {
        var t = TenantWith(maxIpv6: 2);

        Assert.Equal("IPV6_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => Network.Create(t, 0, 2, NetworkName.Parse("net-1"), "singapore01", null, true)).Code);
        Assert.NotNull(Network.Create(t, 0, 2, NetworkName.Parse("net-1"), "singapore01", null, false));
        Assert.NotNull(Network.Create(t, 0, 1, NetworkName.Parse("net-1"), "singapore01", null, true));
    }

    // The uplink of a network with public IPv6 routes only the private ranges.
    [Fact]
    public void Create_WantingPublicIpv6_WithACidrOutsideThePrivateRanges_Refuses()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "singapore01", Ipv4Cidr.Parse("8.8.8.0/24"), true));

        Assert.Equal("NETWORK_CIDR_NOT_PRIVATE", ex.Code);
    }

    [Fact]
    public void CreateGivesUpBeforeIncusHadIt_LetsGoOfTheBlock_AndSaysSo()
    {
        Network network = HoldingBlock64();

        bool letGo = network.MarkRequestFailed("Network creation failed.", "detail");

        Assert.True(letGo);
        Assert.False(network.HasPublicIpv6);
        Assert.Null(network.PublicIpv6Subnet);
        Assert.Null(network.PublicIpv6RouterAddress);
    }

    // Incus has the network, router and all: the block stays until the delete is confirmed.
    [Fact]
    public void CreateGivesUpAfterIncusHadIt_KeepsTheBlock_AndSaysNothingWasLetGo()
    {
        Network network = HoldingBlock64();
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");

        bool letGo = network.MarkRequestFailed("Network creation failed.", "detail");

        Assert.False(letGo);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
    }

    [Fact]
    public void CreateGivesUp_OfANetworkWithoutABlock_SaysNothingWasLetGo()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "mock", null, wantsPublicIpv6: false);

        Assert.False(network.MarkRequestFailed("Network creation failed.", "detail"));
    }

    // A give-up with no create waiting is stale and records nothing: the block it might hold is not its to let go of.
    [Fact]
    public void ARepeatedGiveUp_SaysNothingWasLetGo()
    {
        Network network = HoldingBlock64();
        network.MarkRequestFailed("Network creation failed.", "detail");

        Assert.False(network.MarkRequestFailed("Network creation failed.", "detail"));
    }

    [Fact]
    public void DeleteConfirmed_OfANetworkHoldingABlock_SaysItWasLetGo_AndKeepsTheAddresses()
    {
        Network network = Existing(HoldingBlock64());
        network.RequestDelete();

        NetworkDeletionOutcome outcome = network.MarkDeleted();

        Assert.True(outcome.LetGoOfBlock64);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
    }

    [Fact]
    public void DeleteConfirmed_OfANetworkWithoutABlock_LetsGoOfNothing()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "mock", null, wantsPublicIpv6: false);
        Existing(network).RequestDelete();

        Assert.False(network.MarkDeleted().LetGoOfBlock64);
    }

    // Deleted absorbs: a repeated Deleted report must not take the block back a second time.
    [Fact]
    public void DeleteConfirmedTwice_LetsGoOfTheBlockOnce()
    {
        Network network = Existing(HoldingBlock64());
        network.RequestDelete();
        Assert.True(network.MarkDeleted().LetGoOfBlock64);

        Assert.False(network.MarkDeleted().LetGoOfBlock64);
    }

    [Fact]
    public void DeleteConfirmed_AfterTheCreateGaveUpBeforeIncus_LetsGoOfNothingMore()
    {
        Network network = HoldingBlock64();
        Assert.True(network.MarkRequestFailed("Network creation failed.", null));

        Assert.False(network.MarkDeleted().LetGoOfBlock64);
    }
}

using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// A network's public /64 (titan-ops infra/incus/design.md#ipv6-address-plan): asked for at create, counted against
// MaxPublicIpv6 by the factory, and given back when the create gives up before Incus had the network — otherwise
// a failed create would hold one of the cluster's 255 /64s until the tenant deletes it.
public class NetworkPublicIpv6LifecycleTests
{
    private static readonly NetworkPublicIpv6 Ipv6 = NetworkPublicIpv6.For("2001:41d0:d00:d400::/56", 5);

    private static Tenant TenantWith(int maxNetworks = 9, int maxIpv6 = 9)
    {
        var t = TestTenant.Active("acme", "kc-1", "a@acme.com");
        t.UpdateQuota(TenantQuota.Create(9, maxNetworks, 9, 9, 9, 9, maxIpv6, 9, 9));
        return t;
    }

    [Fact]
    public void Create_WithPublicIpv6_CarriesTheSubnetAndRouter()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "singapore01", null, Ipv6);

        Assert.Equal(5, network.PublicIpv6Index);
        Assert.Equal("2001:41d0:d00:d405::/64", network.PublicIpv6Subnet);
        Assert.Equal("2001:41d0:d00:d400::105", network.PublicIpv6RouterAddress);
    }

    [Fact]
    public void Create_WithoutPublicIpv6_HasNone()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "mock", null, null);

        Assert.Null(network.PublicIpv6Index);
        Assert.Null(network.PublicIpv6Subnet);
        Assert.Null(network.PublicIpv6RouterAddress);
    }

    // The cap pair (QuotaInFactoryTests): at the cap the factory refuses, and only when IPv6 is asked for.
    [Fact]
    public void Create_AtTheIpv6Cap_Refuses_OnlyWhenIpv6IsAsked()
    {
        var t = TenantWith(maxIpv6: 2);

        Assert.Equal("IPV6_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => Network.Create(t, 0, 2, NetworkName.Parse("net-1"), "singapore01", null, Ipv6)).Code);
        Assert.NotNull(Network.Create(t, 0, 2, NetworkName.Parse("net-1"), "singapore01", null, null));
        Assert.NotNull(Network.Create(t, 0, 1, NetworkName.Parse("net-1"), "singapore01", null, Ipv6));
    }

    [Fact]
    public void CreateGivesUpBeforeIncusHadIt_GivesTheSubnetBack()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "singapore01", null, Ipv6);

        network.MarkRequestFailed(null, null, null, "Network creation failed.", "detail");

        Assert.Null(network.PublicIpv6Index);
        Assert.Null(network.PublicIpv6Subnet);
        Assert.Null(network.PublicIpv6RouterAddress);
    }

    // Incus has the network, router and all: the /64 stays until the delete is confirmed.
    [Fact]
    public void CreateGivesUpAfterIncusHadIt_KeepsTheSubnet()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "singapore01", null, Ipv6);
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");

        network.MarkRequestFailed(null, null, null, "Network creation failed.", "detail");

        Assert.Equal(5, network.PublicIpv6Index);
    }

    // The allocator takes back the number given back longest ago (NetworkPublicIpv6Tests), so the delete records when.
    [Fact]
    public void DeleteConfirmed_OfANetworkHoldingA64_RecordsWhenItWasGivenBack()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "singapore01", null, Ipv6);
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");
        network.MarkObserved(NetworkStatus.Created);
        network.RequestDelete();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        network.MarkDeleted();

        Assert.NotNull(network.PublicIpv6ReleasedAt);
        Assert.InRange(network.PublicIpv6ReleasedAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Equal(5, network.PublicIpv6Index);
    }

    [Fact]
    public void DeleteConfirmed_OfANetworkWithoutA64_RecordsNothing()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "mock", null, null);
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");
        network.MarkObserved(NetworkStatus.Created);
        network.RequestDelete();

        network.MarkDeleted();

        Assert.Null(network.PublicIpv6ReleasedAt);
    }

    // Deleted absorbs: a repeated Deleted report must not move the release time forward.
    [Fact]
    public void DeleteConfirmedTwice_KeepsTheFirstReleaseTime()
    {
        var network = Network.Create(TenantWith(), 0, 0, NetworkName.Parse("net-1"), "singapore01", null, Ipv6);
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");
        network.MarkObserved(NetworkStatus.Created);
        network.RequestDelete();
        network.MarkDeleted();
        DateTimeOffset? first = network.PublicIpv6ReleasedAt;

        network.MarkDeleted();

        Assert.Equal(first, network.PublicIpv6ReleasedAt);
    }
}

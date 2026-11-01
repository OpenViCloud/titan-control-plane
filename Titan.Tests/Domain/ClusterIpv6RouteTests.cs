using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops infra/incus/design.md#ipv6-address-plan: route number n of the cluster's /56 goes with the router address
// ::(0x100 + n) of the first /64, the next hop of the static OVH route for that /64. A wrong digit here is a network
// that gets its /64 and never receives a packet.
public class ClusterIpv6RouteTests
{
    private const string Block56 = "2001:41d0:d00:d400::/56";
    private static readonly DateTimeOffset Noon = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClusterIpv6Route Route(int number, Guid? networkId = null, DateTimeOffset? releasedAt = null) =>
        ClusterIpv6Route.CreateUnchecked("singapore01", number, networkId, releasedAt);

    [Theory]
    [InlineData(1, "2001:41d0:d00:d401::/64", "2001:41d0:d00:d400::101")]
    [InlineData(16, "2001:41d0:d00:d410::/64", "2001:41d0:d00:d400::110")]
    [InlineData(112, "2001:41d0:d00:d470::/64", "2001:41d0:d00:d400::170")]
    [InlineData(255, "2001:41d0:d00:d4ff::/64", "2001:41d0:d00:d400::1ff")]
    public void RouteNumberN_PairsSubnetAndRouter(int number, string subnet, string router)
    {
        ClusterIpv6Route route = Route(number);

        Assert.Equal(subnet, route.SubnetIn(Block56));
        Assert.Equal(router, route.RouterAddressIn(Block56));
    }

    // Re-creating a router on the address of one deleted minutes before leaves the new network dark for minutes
    // (titan-ops features/network/design.md#ipv6-public): a route never used comes first.
    [Fact]
    public void NextToGive_PrefersARouteNeverUsed_OverOneJustGivenBack()
    {
        ClusterIpv6Route next = ClusterIpv6Route.NextToGive("singapore01", [Route(1, releasedAt: Noon), Route(3)]);

        Assert.Equal(3, next.Number);
    }

    [Fact]
    public void NextToGive_AmongRoutesNeverUsed_TakesTheLowestNumber()
    {
        ClusterIpv6Route next = ClusterIpv6Route.NextToGive("singapore01", [Route(9), Route(4), Route(200)]);

        Assert.Equal(4, next.Number);
    }

    [Fact]
    public void NextToGive_WhenEveryFreeRouteWasUsed_TakesTheOneGivenBackLongestAgo()
    {
        ClusterIpv6Route next = ClusterIpv6Route.NextToGive("singapore01",
            [Route(10, releasedAt: Noon.AddMinutes(-5)), Route(20, releasedAt: Noon.AddMinutes(-30)), Route(30, releasedAt: Noon.AddMinutes(-1))]);

        Assert.Equal(20, next.Number);
    }

    [Fact]
    public void NextToGive_WhenEveryFreeRouteWasUsed_OrderIsTheReleaseTime_NotTheNumber()
    {
        ClusterIpv6Route next = ClusterIpv6Route.NextToGive("singapore01",
            [Route(5, releasedAt: Noon.AddMinutes(-1)), Route(200, releasedAt: Noon.AddMinutes(-90))]);

        Assert.Equal(200, next.Number);
    }

    [Fact]
    public void NextToGive_WhenTwoWereGivenBackAtTheSameTime_TakesTheLowerNumber()
    {
        ClusterIpv6Route next = ClusterIpv6Route.NextToGive("singapore01", [Route(40, releasedAt: Noon), Route(9, releasedAt: Noon)]);

        Assert.Equal(9, next.Number);
    }

    [Fact]
    public void NextToGive_WithNoFreeRoute_Throws422Ipv6NotAvailable()
    {
        var ex = Assert.Throws<BusinessException>(() => ClusterIpv6Route.NextToGive("singapore01", []));

        Assert.Equal("IPV6_NOT_AVAILABLE", ex.Code);
    }

    [Fact]
    public void HandTo_RecordsTheNetwork_AndClearsTheReleaseTime()
    {
        ClusterIpv6Route route = Route(77, releasedAt: Noon);
        Guid network = Guid.NewGuid();

        route.HandTo(network);

        Assert.Equal(network, route.NetworkId);
        Assert.Null(route.ReleasedAt);
        Assert.False(route.IsFree);
    }

    [Fact]
    public void HandTo_ARouteAnotherNetworkHolds_Throws()
    {
        ClusterIpv6Route route = Route(77, Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => route.HandTo(Guid.NewGuid()));
    }

    [Fact]
    public void TakeBack_FreesTheRoute_AndRecordsWhen()
    {
        ClusterIpv6Route route = Route(1, Guid.NewGuid());

        route.TakeBack(Noon);

        Assert.Null(route.NetworkId);
        Assert.Equal(Noon, route.ReleasedAt);
        Assert.True(route.IsFree);
    }

    [Fact]
    public void TakeBack_WithoutATime_StampsNow()
    {
        ClusterIpv6Route route = Route(1, Guid.NewGuid());
        DateTimeOffset before = DateTimeOffset.UtcNow;

        route.TakeBack();

        Assert.True(route.ReleasedAt >= before);
    }

    [Fact]
    public void TakeBack_OfAFreeRoute_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Route(1).TakeBack(Noon));
    }

    // The first /64 holds OVH's gateway and the routers, and the /56 has 255 more.
    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void CreateUnchecked_RefusesANumberOutsideOneTo255(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterIpv6Route.CreateUnchecked("singapore01", number));
    }
}

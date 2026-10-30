using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops infra/incus/design.md#ipv6-address-plan: /64 number n of the cluster's /56 goes with the router address
// ::(0x100 + n) of the first /64, the next hop of the static OVH route for that /64. A wrong digit here is a
// network that gets its /64 and never receives a packet.
public class NetworkPublicIpv6Tests
{
    private const string Block = "2001:41d0:d00:d400::/56";
    private static readonly Dictionary<int, DateTimeOffset?> NoneReleased = new();
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstFree_InAnEmptyCluster_IsNumberOne()
    {
        var ipv6 = NetworkPublicIpv6.FirstFree(Block, new HashSet<int>(), NoneReleased);

        Assert.NotNull(ipv6);
        Assert.Equal(1, ipv6.Index);
        Assert.Equal("2001:41d0:d00:d401::/64", ipv6.Subnet);
        Assert.Equal("2001:41d0:d00:d400::101", ipv6.RouterAddress);
    }

    [Theory]
    [InlineData(16, "2001:41d0:d00:d410::/64", "2001:41d0:d00:d400::110")]
    [InlineData(112, "2001:41d0:d00:d470::/64", "2001:41d0:d00:d400::170")]
    [InlineData(255, "2001:41d0:d00:d4ff::/64", "2001:41d0:d00:d400::1ff")]
    public void For_NumberN_PairsSubnetAndRouter(int index, string subnet, string router)
    {
        var ipv6 = NetworkPublicIpv6.For(Block, index);

        Assert.Equal(index, ipv6.Index);
        Assert.Equal(subnet, ipv6.Subnet);
        Assert.Equal(router, ipv6.RouterAddress);
    }

    [Fact]
    public void FirstFree_SkipsTakenNumbers_AndTakesTheLowestGap()
    {
        var ipv6 = NetworkPublicIpv6.FirstFree(Block, new HashSet<int> { 1, 2, 4 }, NoneReleased);

        Assert.NotNull(ipv6);
        Assert.Equal(3, ipv6.Index);
    }

    [Fact]
    public void FirstFree_WhenEveryNumberIsTaken_IsNull()
    {
        var all = Enumerable.Range(1, 255).ToHashSet();

        Assert.Null(NetworkPublicIpv6.FirstFree(Block, all, NoneReleased));
    }

    // The first /64 holds OVH's gateway and the routers; no network may take it.
    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void For_OutsideOneTo255_IsRefused(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkPublicIpv6.For(Block, index));
    }

    // A router re-created on the address of one deleted minutes before is unreachable for minutes (OVH keeps the old
    // router for that next hop): a number given back is taken again only once every free number has been used.
    [Fact]
    public void FirstFree_PrefersANumberNeverUsed_OverOneJustGivenBack()
    {
        var released = new Dictionary<int, DateTimeOffset?> { [1] = Now };

        var ipv6 = NetworkPublicIpv6.FirstFree(Block, new HashSet<int>(), released);

        Assert.NotNull(ipv6);
        Assert.Equal(2, ipv6.Index);
    }

    [Fact]
    public void FirstFree_WhenEveryFreeNumberWasUsed_TakesTheOneGivenBackLongestAgo()
    {
        var taken = Enumerable.Range(1, 255).Where(n => n != 1 && n != 3).ToHashSet();
        var released = new Dictionary<int, DateTimeOffset?> { [1] = Now, [3] = Now.AddHours(-2) };

        var ipv6 = NetworkPublicIpv6.FirstFree(Block, taken, released);

        Assert.NotNull(ipv6);
        Assert.Equal(3, ipv6.Index);
    }

    // The mirror of the test above, so the order is the release time and not the number.
    [Fact]
    public void FirstFree_WhenEveryFreeNumberWasUsed_OrderIsTheReleaseTime_NotTheNumber()
    {
        var taken = Enumerable.Range(1, 255).Where(n => n != 1 && n != 3).ToHashSet();
        var released = new Dictionary<int, DateTimeOffset?> { [1] = Now.AddHours(-2), [3] = Now };

        var ipv6 = NetworkPublicIpv6.FirstFree(Block, taken, released);

        Assert.NotNull(ipv6);
        Assert.Equal(1, ipv6.Index);
    }

    // Rows deleted before the release time was recorded have none: they were given back before any recorded one.
    [Fact]
    public void FirstFree_AReleaseWithNoTime_CountsAsTheOldest()
    {
        var taken = Enumerable.Range(1, 255).Where(n => n != 1 && n != 3).ToHashSet();
        var released = new Dictionary<int, DateTimeOffset?> { [1] = Now.AddDays(-30), [3] = null };

        var ipv6 = NetworkPublicIpv6.FirstFree(Block, taken, released);

        Assert.NotNull(ipv6);
        Assert.Equal(3, ipv6.Index);
    }
}

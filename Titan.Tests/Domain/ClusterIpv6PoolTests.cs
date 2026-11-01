using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops infra/incus/design.md#ipv6-address-plan: /64 number n of the cluster's /56 goes with the router address
// ::(0x100 + n) of the first /64, the next hop of the static OVH route for that /64. A wrong digit here is a
// network that gets its /64 and never receives a packet. The pool owns which numbers are held and when each was
// given back (titan-ops features/network/design.md#ipv6-public).
public class ClusterIpv6PoolTests
{
    private const string Block56 = "2001:41d0:d00:d400::/56";
    private static readonly DateTimeOffset Noon = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClusterIpv6Pool EmptyPool() => ClusterIpv6Pool.Create("singapore01");

    // A pool whose first `count` numbers are held, each by its own network.
    private static ClusterIpv6Pool HoldingTheFirst(int count)
    {
        ClusterIpv6Pool pool = EmptyPool();
        for (int taken = 0; taken < count; taken++)
        {
            pool.GiveBlock64To(Guid.NewGuid(), Block56);
        }
        return pool;
    }

    private static Ipv6Block64Holding HoldingOf(ClusterIpv6Pool pool, int number) => pool.Holdings.Single(s => s.Number == number);

    [Fact]
    public void GiveBlock64_InAnEmptyPool_IsNumberOne_WithItsSubnetAndRouter()
    {
        Guid network = Guid.NewGuid();

        GrantedBlock64 grant = EmptyPool().GiveBlock64To(network, Block56);

        Assert.Equal(1, grant.Number);
        Assert.Equal("2001:41d0:d00:d401::/64", grant.Subnet);
        Assert.Equal("2001:41d0:d00:d400::101", grant.RouterAddress);
    }

    [Theory]
    [InlineData(16, "2001:41d0:d00:d410::/64", "2001:41d0:d00:d400::110")]
    [InlineData(112, "2001:41d0:d00:d470::/64", "2001:41d0:d00:d400::170")]
    [InlineData(255, "2001:41d0:d00:d4ff::/64", "2001:41d0:d00:d400::1ff")]
    public void GiveBlock64_NumberN_PairsSubnetAndRouter(int number, string subnet, string router)
    {
        ClusterIpv6Pool pool = HoldingTheFirst(number - 1);

        GrantedBlock64 grant = pool.GiveBlock64To(Guid.NewGuid(), Block56);

        Assert.Equal(number, grant.Number);
        Assert.Equal(subnet, grant.Subnet);
        Assert.Equal(router, grant.RouterAddress);
    }

    [Fact]
    public void GiveBlock64_RecordsWhoHoldsTheNumber()
    {
        ClusterIpv6Pool pool = EmptyPool();
        Guid network = Guid.NewGuid();

        pool.GiveBlock64To(network, Block56);

        Assert.True(pool.IsBlock64HeldBy(network));
        Assert.Equal(network, HoldingOf(pool, 1).HolderNetworkId);
        Assert.Null(HoldingOf(pool, 1).GivenBackAt);
        Assert.False(pool.IsBlock64HeldBy(Guid.NewGuid()));
    }

    [Fact]
    public void GiveBlock64_SkipsHeldNumbers_AndTakesTheLowestNeverHeld()
    {
        ClusterIpv6Pool pool = HoldingTheFirst(3);
        Guid second = HoldingOf(pool, 2).HolderNetworkId!.Value;
        pool.TakeBlock64BackFrom(second);
        pool.GiveBlock64To(Guid.NewGuid(), Block56); // takes 4, the lowest never held, not the 2 just given back

        Assert.Equal(4, pool.Holdings.Max(s => s.Number));
    }

    [Fact]
    public void GiveBlock64_WhenEveryNumberIsHeld_Throws422Ipv6NotAvailable_AndChangesNothing()
    {
        ClusterIpv6Pool pool = HoldingTheFirst(255);
        int versionBefore = pool.Version;

        var ex = Assert.Throws<BusinessException>(() => pool.GiveBlock64To(Guid.NewGuid(), Block56));

        Assert.Equal("IPV6_NOT_AVAILABLE", ex.Code);
        Assert.Equal(255, pool.Holdings.Count);
        Assert.Equal(versionBefore, pool.Version);
    }

    // Re-creating a router on the address of one deleted minutes before leaves the new network dark for minutes
    // (titan-ops features/network/design.md#ipv6-public): a number never held comes first.
    [Fact]
    public void GiveBlock64_PrefersANumberNeverUsed_OverOneJustGivenBack()
    {
        ClusterIpv6Pool pool = ClusterIpv6Pool.CreateUnchecked("singapore01",
            Holding(1, held: false, givenBackAt: Noon),
            Holding(2, held: true));

        GrantedBlock64 grant = pool.GiveBlock64To(Guid.NewGuid(), Block56);

        Assert.Equal(3, grant.Number);
    }

    [Fact]
    public void GiveBlock64_WhenEveryFreeNumberWasUsed_TakesTheOneGivenBackLongestAgo()
    {
        ClusterIpv6Pool pool = FullWith(
            (10, Noon.AddMinutes(-5)),
            (20, Noon.AddMinutes(-30)),
            (30, Noon.AddMinutes(-1)));

        GrantedBlock64 grant = pool.GiveBlock64To(Guid.NewGuid(), Block56);

        Assert.Equal(20, grant.Number);
    }

    [Fact]
    public void GiveBlock64_WhenEveryFreeNumberWasUsed_OrderIsTheGivenBackTime_NotTheNumber()
    {
        ClusterIpv6Pool pool = FullWith(
            (5, Noon.AddMinutes(-1)),
            (200, Noon.AddMinutes(-90)));

        Assert.Equal(200, pool.GiveBlock64To(Guid.NewGuid(), Block56).Number);
    }

    [Fact]
    public void GiveBlock64_AGivenBackBlock64WithNoTime_CountsAsTheOldest()
    {
        ClusterIpv6Pool pool = FullWith(
            (7, Noon.AddMinutes(-600)),
            (150, null));

        Assert.Equal(150, pool.GiveBlock64To(Guid.NewGuid(), Block56).Number);
    }

    [Fact]
    public void GiveBlock64_WhenTwoWereGivenBackAtTheSameTime_TakesTheLowerNumber()
    {
        ClusterIpv6Pool pool = FullWith(
            (40, Noon),
            (9, Noon));

        Assert.Equal(9, pool.GiveBlock64To(Guid.NewGuid(), Block56).Number);
    }

    [Fact]
    public void GiveBlock64_AGivenBackBlock64_IsHeldAgain_WithNoGivenBackTime()
    {
        ClusterIpv6Pool pool = FullWith((77, Noon.AddMinutes(-3)));
        Guid network = Guid.NewGuid();

        pool.GiveBlock64To(network, Block56);

        Assert.Equal(network, HoldingOf(pool, 77).HolderNetworkId);
        Assert.Null(HoldingOf(pool, 77).GivenBackAt);
        Assert.Equal(255, pool.Holdings.Count);
    }

    [Fact]
    public void GiveBlock64_ForANetworkThatAlreadyHoldsOne_Throws()
    {
        ClusterIpv6Pool pool = EmptyPool();
        Guid network = Guid.NewGuid();
        pool.GiveBlock64To(network, Block56);

        Assert.Throws<InvalidOperationException>(() => pool.GiveBlock64To(network, Block56));
    }

    [Fact]
    public void TakeBlock64Back_RecordsWhenTheNumberWasGivenBack_AndWhoHoldsNothingNow()
    {
        ClusterIpv6Pool pool = EmptyPool();
        Guid network = Guid.NewGuid();
        pool.GiveBlock64To(network, Block56);

        pool.TakeBlock64BackFrom(network, Noon);

        Assert.False(pool.IsBlock64HeldBy(network));
        Assert.Null(HoldingOf(pool, 1).HolderNetworkId);
        Assert.Equal(Noon, HoldingOf(pool, 1).GivenBackAt);
    }

    [Fact]
    public void TakeBlock64Back_OfANetworkThatHoldsNothing_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => EmptyPool().TakeBlock64BackFrom(Guid.NewGuid(), Noon));
    }

    [Fact]
    public void TakeBlock64Back_WithoutATime_StampsNow()
    {
        ClusterIpv6Pool pool = EmptyPool();
        Guid network = Guid.NewGuid();
        pool.GiveBlock64To(network, Block56);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        pool.TakeBlock64BackFrom(network);

        Assert.True(HoldingOf(pool, 1).GivenBackAt >= before);
    }

    // Two requests that loaded the pool at the same version meet at the version token (titan-ops
    // features/network/design.md#ipv6-public): each change moves it.
    [Fact]
    public void GiveAndTakeBack_EachMoveTheVersion()
    {
        ClusterIpv6Pool pool = EmptyPool();
        Guid network = Guid.NewGuid();
        int start = pool.Version;

        pool.GiveBlock64To(network, Block56);
        int afterGive = pool.Version;
        pool.TakeBlock64BackFrom(network, Noon);

        Assert.True(afterGive > start);
        Assert.True(pool.Version > afterGive);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_WithoutAClusterId_Throws(string clusterId)
    {
        Assert.Throws<ArgumentException>(() => ClusterIpv6Pool.Create(clusterId));
    }

    private static (int Number, bool Held, DateTimeOffset? GivenBackAt) Holding(int number, bool held, DateTimeOffset? givenBackAt = null) =>
        (number, held, givenBackAt);

    // A pool in which every number but the ones named holds a network; the named ones were given back at the time shown.
    private static ClusterIpv6Pool FullWith(params (int Number, DateTimeOffset? GivenBackAt)[] givenBack)
    {
        var givenBackNumbers = givenBack.Select(g => g.Number).ToHashSet();
        var holdings = new List<(int Number, bool Held, DateTimeOffset? GivenBackAt)>();
        for (int number = 1; number <= 255; number++)
        {
            if (!givenBackNumbers.Contains(number))
            {
                holdings.Add(Holding(number, held: true));
            }
        }
        foreach (var released in givenBack)
        {
            holdings.Add(Holding(released.Number, held: false, givenBackAt: released.GivenBackAt));
        }
        return ClusterIpv6Pool.CreateUnchecked("singapore01", holdings.ToArray());
    }
}

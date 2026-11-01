using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Infrastructure.Endpoints;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// One pool per cluster owns which networks hold which /64 number (titan-ops features/network/design.md#ipv6-public).
/// Two requests that loaded the pool at the same version both pass the handler; only the database stops the later
/// commit, and ErrorStatus turns that into 409 IPV6_ALLOCATION_CONFLICT. InMemory enforces no key and no number, so only
/// Postgres shows it.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ClusterIpv6PoolDbTests(PostgresFixture pg)
{
    private const string Block56 = "2001:41d0:d00:d400::/56";
    private static readonly DateTimeOffset Noon = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static string NewCluster() => $"cluster-{Guid.NewGuid():N}";

    private async Task<int> GiveBlock64AndSaveAsync(string cluster, Guid network)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ClusterIpv6Pool pool = await new ClusterIpv6PoolRepository(db).FindOrAddAsync(cluster);
        GrantedBlock64 grant = pool.GiveBlock64To(network, Block56);
        await db.SaveChangesAsync();
        return grant.Number;
    }

    private async Task<ClusterIpv6Pool> LoadAsync(string cluster)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ClusterIpv6Pool? pool = await new ClusterIpv6PoolRepository(db).FindAsync(cluster);
        Assert.NotNull(pool);
        return pool;
    }

    [Fact]
    public async Task APool_RoundTrips_WithItsHoldings()
    {
        string cluster = NewCluster();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        await GiveBlock64AndSaveAsync(cluster, first);
        await GiveBlock64AndSaveAsync(cluster, second);

        ClusterIpv6Pool pool = await LoadAsync(cluster);
        Assert.Equal(2, pool.Version);
        Assert.Equal(first, pool.Holdings.Single(s => s.Number == 1).HolderNetworkId);
        Assert.Equal(second, pool.Holdings.Single(s => s.Number == 2).HolderNetworkId);
    }

    [Fact]
    public async Task AGivenBackBlock64_KeepsItsGivenBackTime_AcrossALoad()
    {
        string cluster = NewCluster();
        Guid network = Guid.NewGuid();
        await GiveBlock64AndSaveAsync(cluster, network);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            ClusterIpv6Pool pool = (await new ClusterIpv6PoolRepository(db).FindAsync(cluster))!;
            pool.TakeBlock64BackFrom(network, Noon);
            await db.SaveChangesAsync();
        }

        Ipv6Block64Holding holding = (await LoadAsync(cluster)).Holdings.Single();
        Assert.Null(holding.HolderNetworkId);
        Assert.Equal(Noon, holding.GivenBackAt);
    }

    [Fact]
    public async Task TwoGivesFromTheSameLoadedPool_TheSecondSaveIs409Ipv6AllocationConflict()
    {
        string cluster = NewCluster();
        await GiveBlock64AndSaveAsync(cluster, Guid.NewGuid());
        Guid winner = Guid.NewGuid();
        Guid loser = Guid.NewGuid();

        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        ClusterIpv6Pool poolOfFirst = (await new ClusterIpv6PoolRepository(first).FindAsync(cluster))!;
        ClusterIpv6Pool poolOfSecond = (await new ClusterIpv6PoolRepository(second).FindAsync(cluster))!;
        poolOfFirst.GiveBlock64To(winner, Block56);
        poolOfSecond.GiveBlock64To(loser, Block56);
        await first.SaveChangesAsync();

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => second.SaveChangesAsync());

        Assert.Equal(new ErrorStatus(409, ErrorStatus.Ipv6AllocationConflictCode), ErrorStatus.For(ex));
        ClusterIpv6Pool stored = await LoadAsync(cluster);
        Assert.Equal(2, stored.Holdings.Count);
        Assert.True(stored.IsBlock64HeldBy(winner));
        Assert.False(stored.IsBlock64HeldBy(loser));

        // Asking again is the way out of the 409: the loser gets the next block, not the winner's.
        Assert.Equal(3, await GiveBlock64AndSaveAsync(cluster, loser));
    }

    // Both requests update the same holding row, so no key stops the later one: only the version does, or its network
    // silently takes the number the first one just gave away.
    [Fact]
    public async Task TwoGivesOfTheSameGivenBackBlock64_TheSecondSaveIs409Ipv6AllocationConflict()
    {
        string cluster = NewCluster();
        // Every number but 1 is held, and 1 was given back: the only number either request can take.
        var holdings = new List<(int Number, bool Held, DateTimeOffset? GivenBackAt)> { (1, false, Noon) };
        for (int number = 2; number <= 255; number++)
        {
            holdings.Add((number, true, null));
        }
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.ClusterIpv6Pools.Add(ClusterIpv6Pool.CreateUnchecked(cluster, holdings.ToArray()));
            await db.SaveChangesAsync();
        }
        Guid winner = Guid.NewGuid();
        Guid loser = Guid.NewGuid();

        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        ClusterIpv6Pool poolOfFirst = (await new ClusterIpv6PoolRepository(first).FindAsync(cluster))!;
        ClusterIpv6Pool poolOfSecond = (await new ClusterIpv6PoolRepository(second).FindAsync(cluster))!;
        Assert.Equal(1, poolOfFirst.GiveBlock64To(winner, Block56).Number);
        Assert.Equal(1, poolOfSecond.GiveBlock64To(loser, Block56).Number);
        await first.SaveChangesAsync();

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => second.SaveChangesAsync());

        Assert.Equal(new ErrorStatus(409, ErrorStatus.Ipv6AllocationConflictCode), ErrorStatus.For(ex));
        Assert.Equal(winner, (await LoadAsync(cluster)).Holdings.Single(s => s.Number == 1).HolderNetworkId);
    }

    [Fact]
    public async Task TheFirstEverGiveOfACluster_RacedByTwo_TheSecondIs409Ipv6AllocationConflict()
    {
        string cluster = NewCluster();
        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        ClusterIpv6Pool poolOfFirst = await new ClusterIpv6PoolRepository(first).FindOrAddAsync(cluster);
        ClusterIpv6Pool poolOfSecond = await new ClusterIpv6PoolRepository(second).FindOrAddAsync(cluster);
        poolOfFirst.GiveBlock64To(Guid.NewGuid(), Block56);
        poolOfSecond.GiveBlock64To(Guid.NewGuid(), Block56);
        await first.SaveChangesAsync();

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => second.SaveChangesAsync());

        Assert.Equal(new ErrorStatus(409, ErrorStatus.Ipv6AllocationConflictCode), ErrorStatus.For(ex));
        Assert.Single((await LoadAsync(cluster)).Holdings);
    }

    [Fact]
    public async Task TheSameBlock64Number_InTwoClusters_IsFine()
    {
        int inA = await GiveBlock64AndSaveAsync(NewCluster(), Guid.NewGuid());
        int inB = await GiveBlock64AndSaveAsync(NewCluster(), Guid.NewGuid());

        Assert.Equal(1, inA);
        Assert.Equal(1, inB);
    }

    [Fact]
    public async Task ANetwork_CannotHoldABlock64InTwoPools_At409Ipv6AllocationConflict()
    {
        Guid network = Guid.NewGuid();
        await GiveBlock64AndSaveAsync(NewCluster(), network);

        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => GiveBlock64AndSaveAsync(NewCluster(), network));

        Assert.Equal(new ErrorStatus(409, ErrorStatus.Ipv6AllocationConflictCode), ErrorStatus.For(ex));
    }

    [Fact]
    public async Task AClusterWithoutAPool_IsNull_ForFind_AndNewForFindOrAdd()
    {
        string cluster = NewCluster();
        await using TitanDbContext db = pg.NewDbContext();
        var repo = new ClusterIpv6PoolRepository(db);

        Assert.Null(await repo.FindAsync(cluster));
        ClusterIpv6Pool added = await repo.FindOrAddAsync(cluster);

        Assert.Equal(cluster, added.ClusterId);
        Assert.Empty(added.Holdings);
    }
}

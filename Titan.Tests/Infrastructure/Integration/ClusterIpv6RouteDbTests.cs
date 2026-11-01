using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// Two creates in one cluster lock the free routes one after the other, so neither sees a route the other took and
/// neither is refused; a report that gives a route back writes a held row, which no create locks. InMemory has no lock,
/// so only Postgres shows it.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ClusterIpv6RouteDbTests(PostgresFixture pg)
{
    private static readonly TimeSpan LongerThanALock = TimeSpan.FromSeconds(30);

    private static string NewCluster() => $"cluster-{Guid.NewGuid():N}";

    private async Task<string> ClusterWithRoutesAsync()
    {
        string cluster = NewCluster();
        await using TitanDbContext db = pg.NewDbContext();
        await new ClusterIpv6RouteRepository(db).EnsureRoutesAsync(cluster);
        return cluster;
    }

    private async Task<int> GiveAndSaveAsync(string cluster, Guid network)
    {
        await using TitanDbContext db = pg.NewDbContext();
        IReadOnlyList<ClusterIpv6Route> free = await new ClusterIpv6RouteRepository(db).LockFreeAsync(cluster);
        ClusterIpv6Route route = ClusterIpv6Route.NextToGive(cluster, free);
        route.HandTo(network);
        await db.SaveChangesAsync();
        return route.Number;
    }

    private async Task TakeBackAndSaveAsync(Guid network)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ClusterIpv6Route route = (await new ClusterIpv6RouteRepository(db).FindHeldByAsync(network))!;
        route.TakeBack(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
    }

    private async Task<List<ClusterIpv6Route>> RoutesOfAsync(string cluster)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await db.ClusterIpv6Routes.Where(r => r.ClusterId == cluster).OrderBy(r => r.Number).ToListAsync();
    }

    [Fact]
    public async Task EnsureRoutes_MakesEveryRouteOfTheCluster_Free()
    {
        string cluster = await ClusterWithRoutesAsync();

        List<ClusterIpv6Route> routes = await RoutesOfAsync(cluster);

        Assert.Equal(Enumerable.Range(1, 255), routes.Select(r => r.Number));
        Assert.All(routes, r => Assert.True(r.IsFree));
        Assert.All(routes, r => Assert.Null(r.ReleasedAt));
    }

    [Fact]
    public async Task EnsureRoutes_RunAgain_LeavesAHeldRouteAlone()
    {
        string cluster = await ClusterWithRoutesAsync();
        Guid network = Guid.NewGuid();
        int number = await GiveAndSaveAsync(cluster, network);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            await new ClusterIpv6RouteRepository(db).EnsureRoutesAsync(cluster);
        }

        List<ClusterIpv6Route> routes = await RoutesOfAsync(cluster);
        Assert.Equal(255, routes.Count);
        Assert.Equal(network, routes.Single(r => r.Number == number).NetworkId);
    }

    [Fact]
    public async Task EnsureRoutes_AddsOnlyTheRoutesThatAreMissing()
    {
        string cluster = NewCluster();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO core.cluster_ipv6_routes (cluster_id, number, released_at) VALUES ({cluster}, 7, {DateTimeOffset.UtcNow})");
            await new ClusterIpv6RouteRepository(db).EnsureRoutesAsync(cluster);
        }

        List<ClusterIpv6Route> routes = await RoutesOfAsync(cluster);
        Assert.Equal(255, routes.Count);
        Assert.NotNull(routes.Single(r => r.Number == 7).ReleasedAt);
    }

    [Fact]
    public async Task TheFirstGive_IsRouteOne_AndAReleasedRouteWaitsBehindTheOnesNeverUsed()
    {
        string cluster = await ClusterWithRoutesAsync();
        Guid first = Guid.NewGuid();
        Assert.Equal(1, await GiveAndSaveAsync(cluster, first));

        await TakeBackAndSaveAsync(first);

        Assert.Equal(2, await GiveAndSaveAsync(cluster, Guid.NewGuid()));
        Assert.NotNull((await RoutesOfAsync(cluster)).Single(r => r.Number == 1).ReleasedAt);
    }

    [Fact]
    public async Task TheSameNumber_InTwoClusters_IsFine()
    {
        Assert.Equal(1, await GiveAndSaveAsync(await ClusterWithRoutesAsync(), Guid.NewGuid()));
        Assert.Equal(1, await GiveAndSaveAsync(await ClusterWithRoutesAsync(), Guid.NewGuid()));
    }

    [Fact]
    public async Task EveryRouteHeld_LeavesNothingToGive()
    {
        string cluster = await ClusterWithRoutesAsync();
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE core.cluster_ipv6_routes SET network_id = gen_random_uuid() WHERE cluster_id = {cluster}");

        Assert.Empty(await new ClusterIpv6RouteRepository(db).LockFreeAsync(cluster));
    }

    [Fact]
    public async Task AClusterWithNoRows_HasNothingToGive()
    {
        await using TitanDbContext db = pg.NewDbContext();

        Assert.Empty(await new ClusterIpv6RouteRepository(db).LockFreeAsync(NewCluster()));
    }

    // Every route handed out is one this create holds locked: another connection can lock none of them. It does not
    // reach the case the one statement exists for, a route freed between a lock and a later read.
    [Fact]
    public async Task LockFree_LocksEveryRouteItHandsOut()
    {
        string cluster = await ClusterWithRoutesAsync();
        await using TitanDbContext creating = pg.NewDbContext();
        IReadOnlyList<ClusterIpv6Route> free = await new ClusterIpv6RouteRepository(creating).LockFreeAsync(cluster);
        Assert.Equal(255, free.Count);

        await using TitanDbContext other = pg.NewDbContext();
        int lockableByAnother = await other.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM (SELECT number FROM core.cluster_ipv6_routes WHERE cluster_id = {cluster} FOR UPDATE SKIP LOCKED) lockable")
            .SingleAsync();

        Assert.Equal(0, lockableByAnother);
    }

    [Fact]
    public async Task FindHeldBy_AfterTheRouteWentBack_IsNull()
    {
        string cluster = await ClusterWithRoutesAsync();
        Guid network = Guid.NewGuid();
        await GiveAndSaveAsync(cluster, network);
        await TakeBackAndSaveAsync(network);

        await using TitanDbContext db = pg.NewDbContext();

        Assert.Null(await new ClusterIpv6RouteRepository(db).FindHeldByAsync(network));
    }

    [Fact]
    public async Task ANetwork_CannotHoldTwoRoutes()
    {
        Guid network = Guid.NewGuid();
        await GiveAndSaveAsync(await ClusterWithRoutesAsync(), network);
        string other = await ClusterWithRoutesAsync();

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => GiveAndSaveAsync(other, network));
    }

    // The second create waits at its lock until the first commits, and its statement then leaves out the row the first
    // took; otherwise both would save the same route.
    [Fact]
    public async Task TwoCreatesAtOnce_TheSecondWaits_ThenTakesTheNextRoute()
    {
        string cluster = await ClusterWithRoutesAsync();
        Guid firstNetwork = Guid.NewGuid();
        Guid secondNetwork = Guid.NewGuid();

        await using TitanDbContext first = pg.NewDbContext();
        ClusterIpv6Route routeOfFirst = ClusterIpv6Route.NextToGive(cluster, await new ClusterIpv6RouteRepository(first).LockFreeAsync(cluster));
        routeOfFirst.HandTo(firstNetwork);

        Task<int> second = GiveAndSaveAsync(cluster, secondNetwork);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(second.IsCompleted);

        await first.SaveChangesAsync();
        int numberOfSecond = await second.WaitAsync(LongerThanALock);

        Assert.Equal(1, routeOfFirst.Number);
        Assert.Equal(2, numberOfSecond);
        List<ClusterIpv6Route> routes = await RoutesOfAsync(cluster);
        Assert.Equal(firstNetwork, routes.Single(r => r.Number == 1).NetworkId);
        Assert.Equal(secondNetwork, routes.Single(r => r.Number == 2).NetworkId);
    }

    [Fact]
    public async Task ManyCreatesAtOnce_EachGetsItsOwnRoute()
    {
        string cluster = await ClusterWithRoutesAsync();
        List<Guid> networks = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();

        int[] numbers = await Task.WhenAll(networks.Select(network => GiveAndSaveAsync(cluster, network)));

        Assert.Equal(Enumerable.Range(1, 12), numbers.OrderBy(n => n));
    }

    // A create holds the free routes while a delete report gives a held route back: the report writes a row the create
    // did not lock, so it finishes first, and the create still commits.
    [Fact]
    public async Task AReportGivingARouteBack_DoesNotWaitForACreate_AndNeitherIsRefused()
    {
        string cluster = await ClusterWithRoutesAsync();
        Guid leaving = Guid.NewGuid();
        await GiveAndSaveAsync(cluster, leaving);

        await using TitanDbContext creating = pg.NewDbContext();
        ClusterIpv6Route routeOfCreate = ClusterIpv6Route.NextToGive(cluster, await new ClusterIpv6RouteRepository(creating).LockFreeAsync(cluster));
        Guid arriving = Guid.NewGuid();
        routeOfCreate.HandTo(arriving);

        await TakeBackAndSaveAsync(leaving).WaitAsync(LongerThanALock);
        await creating.SaveChangesAsync();

        List<ClusterIpv6Route> routes = await RoutesOfAsync(cluster);
        Assert.True(routes.Single(r => r.Number == 1).IsFree);
        Assert.Equal(arriving, routes.Single(r => r.Number == 2).NetworkId);
    }
}

using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The migration that moves the numbers a network holds to the cluster's pool runs once, on tables that may hold real
/// networks. A dev database may hold none, so these tests give the
/// SQL the rows it would otherwise never meet: the migration's own BackfillSql is run here, on networks seeded by hand.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ClusterIpv6PoolBackfillDbTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Earlier = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

    private static string NewCluster() => $"cluster-{Guid.NewGuid():N}";

    // A network row that still carries its block number in its own columns.
    private async Task<Guid> SeedAsync(string cluster, int? number, string existence, DateTimeOffset? givenBackAt)
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), $"net-{Guid.NewGuid():N}", cluster);
        await using TitanDbContext db = pg.NewDbContext();
        db.Networks.Add(network);
        await db.SaveChangesAsync();
        if (number != null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE core.networks
SET public_ipv6_index = {number}::int, public_ipv6_subnet = {"2001:db8::/64"}, public_ipv6_router_address = {"2001:db8::101"},
    current_existence = {existence}, public_ipv6_released_at = {givenBackAt}::timestamptz
WHERE id = {network.Id}");
        }
        return network.Id;
    }

    private async Task BackfillAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(Titan.Infrastructure.Migrations.ClusterIpv6Pool.BackfillSql);
    }

    private async Task<ClusterIpv6Pool?> PoolOfAsync(string cluster)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await new ClusterIpv6PoolRepository(db).FindAsync(cluster);
    }

    [Fact]
    public async Task ANumberHeldByALiveNetwork_IsHeldByIt()
    {
        string cluster = NewCluster();
        Guid live = await SeedAsync(cluster, 5, "Exists", null);

        await BackfillAsync();

        Ipv6Block64Holding holding = (await PoolOfAsync(cluster))!.Holdings.Single();
        Assert.Equal(5, holding.Number);
        Assert.Equal(live, holding.HolderNetworkId);
        Assert.Null(holding.GivenBackAt);
    }

    [Fact]
    public async Task ANumberOnlyDeletedNetworksHeld_IsGivenBackAtTheLatestOfThem()
    {
        string cluster = NewCluster();
        await SeedAsync(cluster, 9, "Deleted", Earlier);
        await SeedAsync(cluster, 9, "Deleted", Later);

        await BackfillAsync();

        Ipv6Block64Holding holding = (await PoolOfAsync(cluster))!.Holdings.Single();
        Assert.Equal(9, holding.Number);
        Assert.Null(holding.HolderNetworkId);
        Assert.Equal(Later, holding.GivenBackAt);
    }

    [Fact]
    public async Task ADeletedNetworkWithNoGivenBackTime_IsGivenBackWithNoTime()
    {
        string cluster = NewCluster();
        await SeedAsync(cluster, 12, "Deleted", null);

        await BackfillAsync();

        Ipv6Block64Holding holding = (await PoolOfAsync(cluster))!.Holdings.Single();
        Assert.Null(holding.HolderNetworkId);
        Assert.Null(holding.GivenBackAt);
    }

    [Fact]
    public async Task ANumberHeldNowAndGivenBackBefore_IsHeld()
    {
        string cluster = NewCluster();
        await SeedAsync(cluster, 3, "Deleted", Later);
        Guid live = await SeedAsync(cluster, 3, "Exists", null);

        await BackfillAsync();

        Ipv6Block64Holding holding = (await PoolOfAsync(cluster))!.Holdings.Single();
        Assert.Equal(live, holding.HolderNetworkId);
        Assert.Null(holding.GivenBackAt);
    }

    [Fact]
    public async Task ANetworkWithoutANumber_AddsNoPool()
    {
        string cluster = NewCluster();
        await SeedAsync(cluster, null, "Exists", null);

        await BackfillAsync();

        Assert.Null(await PoolOfAsync(cluster));
    }

    [Fact]
    public async Task RunTwice_ChangesNothing()
    {
        string cluster = NewCluster();
        Guid live = await SeedAsync(cluster, 7, "Exists", null);
        await SeedAsync(cluster, 8, "Deleted", Earlier);

        await BackfillAsync();
        await BackfillAsync();

        ClusterIpv6Pool pool = (await PoolOfAsync(cluster))!;
        Assert.Equal(2, pool.Holdings.Count);
        Assert.Equal(live, pool.Holdings.Single(s => s.Number == 7).HolderNetworkId);
        Assert.Equal(Earlier, pool.Holdings.Single(s => s.Number == 8).GivenBackAt);
    }
}

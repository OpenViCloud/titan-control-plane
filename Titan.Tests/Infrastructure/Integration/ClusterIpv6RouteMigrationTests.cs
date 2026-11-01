using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database whose networks hold /64 numbers in their own columns meets two migrations in one deploy: the one that
/// filled the pool, then the one that moves it to the route table. No env has such a network to try it on, so the
/// numbers are seeded here on the schema before the first of the two, and every migration runs for real.
/// The old table and column names below are the schema before the migration, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class ClusterIpv6RouteMigrationTests : IAsyncLifetime
{
    private const string MigrationBeforeThePool = "20261031013354_NetworkRequestedCidr";
    private const string ThePoolMigration = "20261101025243_ClusterIpv6Pool";
    private const string Cluster = "singapore01";

    private static readonly DateTimeOffset Earlier = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);

    private readonly TestPostgres _container = new();

    public Task InitializeAsync() => _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private async Task MigrateToAsync(string migration)
    {
        await using TitanDbContext db = NewDbContext();
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private async Task MigrateToLatestAsync()
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    // A network row that carries its /64 number in its own columns, as the image before the pool wrote it.
    private async Task<Guid> SeedNetworkAsync(int number, string existence, DateTimeOffset? releasedAt)
    {
        Guid id = Guid.NewGuid();
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.networks (id, tenant_id, name, cluster_id, current_existence, desired_existence, created_at,
                           public_ipv6_index, public_ipv6_subnet, public_ipv6_router_address, public_ipv6_released_at)
VALUES ({id}, {Guid.NewGuid()}, {"net-" + id.ToString("N")}, {Cluster}, {existence}, {existence}, now(),
        {number}, {"2001:db8::/64"}, {"2001:db8::101"}, {releasedAt}::timestamptz)");
        return id;
    }

    private async Task<List<ClusterIpv6Route>> RoutesAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.ClusterIpv6Routes.Where(r => r.ClusterId == Cluster).OrderBy(r => r.Number).ToListAsync();
    }

    private async Task<int> CountTablesNamedAsync(string table)
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = 'core' AND table_name = {table}")
            .SingleAsync();
    }

    [Fact]
    public async Task EveryNumberANetworkHeld_ArrivesOnItsRoute_AndTheOtherRoutesAreThereFree()
    {
        await MigrateToAsync(MigrationBeforeThePool);
        Guid live = await SeedNetworkAsync(5, "Exists", null);
        await SeedNetworkAsync(9, "Deleted", Earlier);
        await SeedNetworkAsync(9, "Deleted", Later);
        await SeedNetworkAsync(3, "Deleted", Later);
        Guid heldAgain = await SeedNetworkAsync(3, "Exists", null);
        await SeedNetworkAsync(12, "Deleted", null);

        await MigrateToLatestAsync();

        List<ClusterIpv6Route> routes = await RoutesAsync();
        Assert.Equal(Enumerable.Range(1, 255), routes.Select(r => r.Number));
        Assert.Equal(live, routes.Single(r => r.Number == 5).NetworkId);
        Assert.Null(routes.Single(r => r.Number == 5).ReleasedAt);
        Assert.True(routes.Single(r => r.Number == 9).IsFree);
        Assert.Equal(Later, routes.Single(r => r.Number == 9).ReleasedAt);
        Assert.Equal(heldAgain, routes.Single(r => r.Number == 3).NetworkId);
        Assert.Null(routes.Single(r => r.Number == 3).ReleasedAt);
        Assert.True(routes.Single(r => r.Number == 12).IsFree);
        Assert.Null(routes.Single(r => r.Number == 12).ReleasedAt);
        // Numbers 3 and 5 are held and 9 went back with a time; 12 went back with none, so it reads as never used.
        Assert.Equal(3, routes.Count(r => !r.IsFree || r.ReleasedAt != null));
    }

    [Fact]
    public async Task ABlockThePoolHoldsOnADatabaseAlreadyOnThePool_ArrivesOnItsRoute()
    {
        await MigrateToAsync(ThePoolMigration);
        Guid network = Guid.NewGuid();
        Guid pool = Guid.NewGuid();
        await using (TitanDbContext db = NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO core.cluster_ipv6_pools (id, cluster_id, version) VALUES ({pool}, {Cluster}, 2)");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO core.cluster_ipv6_block64_holdings (pool_id, block64_number, holder_network_id) VALUES ({pool}, 1, {network})");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO core.cluster_ipv6_block64_holdings (pool_id, block64_number, given_back_at) VALUES ({pool}, 2, {Later})");
        }

        await MigrateToLatestAsync();

        List<ClusterIpv6Route> routes = await RoutesAsync();
        Assert.Equal(255, routes.Count);
        Assert.Equal(network, routes.Single(r => r.Number == 1).NetworkId);
        Assert.Equal(Later, routes.Single(r => r.Number == 2).ReleasedAt);
        Assert.Equal(0, await CountTablesNamedAsync("cluster_ipv6_pools"));
        Assert.Equal(0, await CountTablesNamedAsync("cluster_ipv6_block64_holdings"));
    }

    [Fact]
    public async Task ACleanDatabase_HasNoRouteUntilTheControlPlaneMakesThem()
    {
        await MigrateToLatestAsync();

        Assert.Empty(await RoutesAsync());
    }

    [Fact]
    public async Task Down_PutsWhoHoldsWhatBackInThePool()
    {
        await MigrateToAsync(ThePoolMigration);
        Guid network = Guid.NewGuid();
        Guid pool = Guid.NewGuid();
        await using (TitanDbContext db = NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO core.cluster_ipv6_pools (id, cluster_id, version) VALUES ({pool}, {Cluster}, 1)");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO core.cluster_ipv6_block64_holdings (pool_id, block64_number, holder_network_id) VALUES ({pool}, 4, {network})");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO core.cluster_ipv6_block64_holdings (pool_id, block64_number, given_back_at) VALUES ({pool}, 6, {Later})");
        }
        await MigrateToLatestAsync();

        await MigrateToAsync(ThePoolMigration);

        await using TitanDbContext read = NewDbContext();
        Guid? holder = await read.Database
            .SqlQuery<Guid?>($"SELECT h.holder_network_id AS \"Value\" FROM core.cluster_ipv6_block64_holdings h JOIN core.cluster_ipv6_pools p ON p.id = h.pool_id WHERE p.cluster_id = {Cluster} AND h.block64_number = 4")
            .SingleAsync();
        DateTimeOffset? givenBackAt = await read.Database
            .SqlQuery<DateTimeOffset?>($"SELECT h.given_back_at AS \"Value\" FROM core.cluster_ipv6_block64_holdings h JOIN core.cluster_ipv6_pools p ON p.id = h.pool_id WHERE p.cluster_id = {Cluster} AND h.block64_number = 6")
            .SingleAsync();
        int holdings = await read.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM core.cluster_ipv6_block64_holdings")
            .SingleAsync();
        Assert.Equal(network, holder);
        Assert.Equal(Later, givenBackAt);
        Assert.Equal(2, holdings);
    }
}

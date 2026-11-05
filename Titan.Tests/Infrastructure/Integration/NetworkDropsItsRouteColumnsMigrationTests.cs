using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Tenant;
using Titan.Infrastructure.Persistence;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Tests.Infrastructure.Integration;

/// A database whose networks still carry the three columns the route table replaced meets the migration that drops them:
/// the columns and their unique index go, every network keeps its /64. Down brings them back empty, with the index. The
/// column and index names below are the schema before it, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class NetworkDropsItsRouteColumnsMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261104054835_WidenResourceName";
    private const string ThisMigration = "20261105044846_NetworkDropsItsRouteColumns";
    private const string RouteIndex = "ix_networks_cluster_id_public_ipv6_index";

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

    private async Task<int> CountAsync(string sql)
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync();
    }

    private Task<int> RouteColumnsAsync() => CountAsync(
        "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'networks' AND column_name IN ('public_ipv6_index', 'public_ipv6_router_address', 'public_ipv6_released_at')");

    private Task<int> RouteIndexesAsync() => CountAsync(
        $"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname = 'core' AND tablename = 'networks' AND indexname = '{RouteIndex}'");

    private async Task<NetworkAggregate> SeedNetworkWithItsOldNumberAsync()
    {
        Tenant tenant = TestTenant.Active($"drop-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "drop@titan.local");
        NetworkAggregate network = TestAggregates.Network(tenant, "held-one", $"cluster-{Guid.NewGuid():N}");
        await using TitanDbContext seed = NewDbContext();
        seed.Tenants.Add(tenant);
        seed.Networks.Add(network);
        await seed.SaveChangesAsync();
        await seed.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE core.networks SET public_ipv6_index = 3, public_ipv6_router_address = {"2001:db8:0:3::1"}, public_ipv6_subnet = {"2001:db8:0:3::/64"} WHERE id = {network.Id}");
        return network;
    }

    [Fact]
    public async Task TheColumnsAndTheirIndexGo_EveryNetworkKeepsItsSubnet()
    {
        await MigrateToAsync(MigrationBefore);
        NetworkAggregate network = await SeedNetworkWithItsOldNumberAsync();

        await MigrateToAsync(ThisMigration);

        Assert.Equal(0, await RouteColumnsAsync());
        Assert.Equal(0, await RouteIndexesAsync());
        await using TitanDbContext read = NewDbContext();
        NetworkAggregate stored = await read.Networks.SingleAsync(n => n.Id == network.Id);
        Assert.Equal("2001:db8:0:3::/64", stored.PublicIpv6Subnet);
    }

    [Fact]
    public async Task Down_BringsTheColumnsBackEmpty_WithTheirIndex()
    {
        await MigrateToAsync(MigrationBefore);
        NetworkAggregate network = await SeedNetworkWithItsOldNumberAsync();
        await MigrateToAsync(ThisMigration);

        await MigrateToAsync(MigrationBefore);

        Assert.Equal(3, await RouteColumnsAsync());
        Assert.Equal(1, await RouteIndexesAsync());
        Assert.Equal(0, await CountAsync(
            $"SELECT count(*)::int AS \"Value\" FROM core.networks WHERE id = '{network.Id}' AND (public_ipv6_index IS NOT NULL OR public_ipv6_router_address IS NOT NULL OR public_ipv6_released_at IS NOT NULL)"));
    }
}

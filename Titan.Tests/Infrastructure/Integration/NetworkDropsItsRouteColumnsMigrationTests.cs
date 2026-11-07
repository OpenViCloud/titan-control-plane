using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Infrastructure.Persistence;

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

    // Written by SQL: the model of today has a status column the schema of this migration does not.
    private async Task<Guid> SeedNetworkWithItsOldNumberAsync()
    {
        Guid id = Guid.NewGuid();
        await using TitanDbContext seed = NewDbContext();
        await seed.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.networks (id, tenant_id, name, cluster_id, current_existence, desired_existence, created_at,
                           public_ipv6_index, public_ipv6_router_address, public_ipv6_subnet)
VALUES ({id}, {Guid.NewGuid()}, {"held-" + id.ToString("N")[..8]}, {"cluster-" + id.ToString("N")}, 'Exists', 'Exists', now(),
        3, {"2001:db8:0:3::1"}, {"2001:db8:0:3::/64"})");
        return id;
    }

    [Fact]
    public async Task TheColumnsAndTheirIndexGo_EveryNetworkKeepsItsSubnet()
    {
        await MigrateToAsync(MigrationBefore);
        Guid networkId = await SeedNetworkWithItsOldNumberAsync();

        await MigrateToAsync(ThisMigration);

        Assert.Equal(0, await RouteColumnsAsync());
        Assert.Equal(0, await RouteIndexesAsync());
        await using TitanDbContext read = NewDbContext();
        string subnet = await read.Database
            .SqlQuery<string>($"SELECT public_ipv6_subnet AS \"Value\" FROM core.networks WHERE id = {networkId}").SingleAsync();
        Assert.Equal("2001:db8:0:3::/64", subnet);
    }

    [Fact]
    public async Task Down_BringsTheColumnsBackEmpty_WithTheirIndex()
    {
        await MigrateToAsync(MigrationBefore);
        Guid networkId = await SeedNetworkWithItsOldNumberAsync();
        await MigrateToAsync(ThisMigration);

        await MigrateToAsync(MigrationBefore);

        Assert.Equal(3, await RouteColumnsAsync());
        Assert.Equal(1, await RouteIndexesAsync());
        Assert.Equal(0, await CountAsync(
            $"SELECT count(*)::int AS \"Value\" FROM core.networks WHERE id = '{networkId}' AND (public_ipv6_index IS NOT NULL OR public_ipv6_router_address IS NOT NULL OR public_ipv6_released_at IS NOT NULL)"));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with networks meets the migration that gives a network its SSH flag (titan-ops
/// features/network/design.md#default-network): every network there is not the default one, so it reads false, and a
/// network saved with the flag set reads it back. Down drops the column. The row before the migration is written in
/// SQL: the model maps the column this schema does not have yet.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class NetworkOpensSshWhenCreatedMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261119083333_ProviderCommandResource";

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

    private async Task<Guid> SeedANetworkAsync()
    {
        Guid id = Guid.NewGuid();
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.networks (id, tenant_id, name, cluster_id, status, default_ingress_policy, default_egress_policy, created_at)
VALUES ({id}, {Guid.NewGuid()}, {"net-" + id.ToString("N")[..8]}, 'cluster-a', 'Creating', 'Deny', 'Allow', now())");
        return id;
    }

    private async Task<int> OpensSshWhenCreatedColumnsAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'networks' AND column_name = 'opens_ssh_when_created'")
            .SingleAsync();
    }

    [Fact]
    public async Task EveryNetworkThere_IsNotTheDefaultOne()
    {
        await MigrateToAsync(MigrationBefore);
        Guid networkId = await SeedANetworkAsync();

        await MigrateToLatestAsync();

        await using TitanDbContext db = NewDbContext();
        Network network = await db.Networks.SingleAsync(n => n.Id == networkId);
        Assert.False(network.OpensSshWhenCreated);
    }

    [Fact]
    public async Task ANetworkSavedWithTheFlagSet_ReadsItBack()
    {
        await MigrateToLatestAsync();
        Network network = Network.Create(Guid.NewGuid(), NetworkName.Parse("default"), "cluster-a", null,
            wantsPublicIpv6: false, opensSshWhenCreated: true);
        await using (TitanDbContext write = NewDbContext())
        {
            write.Networks.Add(network);
            await write.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        Assert.True((await read.Networks.SingleAsync(n => n.Id == network.Id)).OpensSshWhenCreated);
    }

    [Fact]
    public async Task Down_DropsTheColumn()
    {
        await MigrateToLatestAsync();
        int after = await OpensSshWhenCreatedColumnsAsync();

        await MigrateToAsync(MigrationBefore);

        Assert.Equal((1, 0), (after, await OpensSshWhenCreatedColumnsAsync()));
    }
}

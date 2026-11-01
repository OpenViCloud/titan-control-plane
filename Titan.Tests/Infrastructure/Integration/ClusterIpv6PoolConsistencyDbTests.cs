using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The network keeps the two addresses of its /64 block and the cluster's pool keeps who holds which block; the two
/// commit in one save, so after any run of creates, give-ups and deletes they agree
/// (titan-ops features/network/design.md#ipv6-public). ChildEntityGuardTests cannot see a handler that saves two
/// aggregates, so this is the test that notices a branch that lets go of a block and forgets to tell the pool.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ClusterIpv6PoolConsistencyDbTests(PostgresFixture pg)
{
    private const string Block56 = "2001:41d0:d00:d400::/56";

    private static ClusterOptions ClusterWithIpv6(string clusterId) => new()
    {
        Clusters =
        [
            new()
            {
                Id = clusterId, Name = clusterId, Endpoint = "https://localhost:8443", ServerFingerprint = new string('a', 64),
                OvnUplinkNetwork = "ovnup", StoragePool = "default", Ipv6Block = Block56,
            },
        ],
    };

    private async Task<Guid> CreateAsync(Guid tenantId, string clusterId, string name)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var caller = Callers.TenantMember(tenantId);
        var handler = new CreateNetworkCommandHandler(
            new NetworkRepository(db), new ClusterIpv6PoolRepository(db), new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller), ClusterWithIpv6(clusterId));
        CreateNetworkCommandResult result = await handler.HandleAsync(
            new CreateNetworkCommand(name, clusterId, EnablePublicIpv6: true));
        return result.NetworkId;
    }

    private async Task ReportAsync(Guid networkId, UpdateNetworkStatusCommand report)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var caller = Callers.TenantMember(Guid.NewGuid());
        var handler = new UpdateNetworkStatusCommandHandler(
            new NetworkRepository(db), new ClusterIpv6PoolRepository(db),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(report);
    }

    private async Task AskForDeleteAsync(Guid networkId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var repo = new NetworkRepository(db);
        Network network = (await repo.FindByIdAsync(networkId))!;
        network.RequestDelete();
        await repo.SaveAsync();
    }

    private async Task ItExistsOnIncusAsync(Guid networkId, string externalId)
    {
        await ReportAsync(networkId, new UpdateNetworkStatusCommand(networkId, "Created", ExternalId: externalId, Cidr: "10.0.0.0/24", Gateway: "10.0.0.1"));
        await ReportAsync(networkId, new UpdateNetworkStatusCommand(networkId, "Observed", Exists: true, Status: "Created"));
    }

    [Fact]
    public async Task EveryLiveNetworkWithASubnet_HoldsExactlyOneBlock64_AndEveryBlock64HeldBelongsToOne()
    {
        string cluster = $"cluster-{Guid.NewGuid():N}";
        Tenant tenant = TestTenant.Active($"pool-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "pool@titan.local", maxNetworks: 10);
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
        }

        Guid deleted = await CreateAsync(tenant.Id, cluster, "deleted-one");
        Guid gaveUp = await CreateAsync(tenant.Id, cluster, "gave-up-one");
        Guid alive = await CreateAsync(tenant.Id, cluster, "alive-one");
        Guid keptAfterGiveUp = await CreateAsync(tenant.Id, cluster, "kept-one");

        await ItExistsOnIncusAsync(deleted, "net-deleted");
        await AskForDeleteAsync(deleted);
        await ReportAsync(deleted, new UpdateNetworkStatusCommand(deleted, "Deleted"));
        await ReportAsync(gaveUp, new UpdateNetworkStatusCommand(gaveUp, "RequestFailed", Reason: "Network creation failed.", Detail: "raw incus error"));
        await ReportAsync(keptAfterGiveUp, new UpdateNetworkStatusCommand(keptAfterGiveUp, "RequestFailed",
            ExternalId: "net-kept", Cidr: "10.1.0.0/24", Gateway: "10.1.0.1", Reason: "Network creation failed.", Detail: "raw incus error"));
        await ReportAsync(deleted, new UpdateNetworkStatusCommand(deleted, "Deleted")); // a repeated report

        await using TitanDbContext read = pg.NewDbContext();
        List<Guid> liveWithASubnet = await read.Networks
            .Where(n => n.ClusterId == cluster && n.CurrentExistence != CurrentExistence.Deleted && n.PublicIpv6Subnet != null)
            .Select(n => n.Id)
            .ToListAsync();
        ClusterIpv6Pool pool = (await new ClusterIpv6PoolRepository(read).FindAsync(cluster))!;
        List<Guid> holders = pool.Holdings.Where(h => h.IsHeld).Select(h => h.HolderNetworkId!.Value).ToList();

        Assert.Equal(liveWithASubnet.OrderBy(id => id), holders.OrderBy(id => id));
        Assert.Equal(holders.Count, holders.Distinct().Count());
        Assert.Equal(new[] { alive, keptAfterGiveUp }.OrderBy(id => id), holders.OrderBy(id => id));
        // The two blocks that went back carry the time they went back; the next network gets a block never held.
        Assert.Equal(2, pool.Holdings.Count(h => !h.IsHeld && h.GivenBackAt != null));
        Assert.Equal(5, await CreateNumberAsync(tenant.Id, cluster));
    }

    // The columns the pool replaces are still on networks until a later migration drops them. A number left in them,
    // with the pool holding nothing, is not held: the pool is the only place that says who holds a block.
    [Fact]
    public async Task ANumberLeftInTheNetworksOwnColumn_IsNotHeld_WhenThePoolHoldsNothing()
    {
        string cluster = $"cluster-{Guid.NewGuid():N}";
        Tenant tenant = TestTenant.Active($"pool-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "pool@titan.local", maxNetworks: 10);
        var legacy = Network.CreateUnchecked(tenant.Id, "legacy-one", cluster);
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            seed.Networks.Add(legacy);
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.networks SET public_ipv6_index = 1, public_ipv6_subnet = {"2001:db8::/64"} WHERE id = {legacy.Id}");
        }

        Assert.Equal(1, await CreateNumberAsync(tenant.Id, cluster));
    }

    private async Task<int> CreateNumberAsync(Guid tenantId, string cluster)
    {
        Guid next = await CreateAsync(tenantId, cluster, "next-one");
        await using TitanDbContext read = pg.NewDbContext();
        ClusterIpv6Pool pool = (await new ClusterIpv6PoolRepository(read).FindAsync(cluster))!;
        return pool.Holdings.Single(h => h.HolderNetworkId == next).Number;
    }
}

using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The network keeps its subnet and the route table keeps who holds which route; the two commit in one save, so after
/// any run of creates, give-ups and deletes they agree. ChildEntityGuardTests cannot see a handler that saves two
/// aggregates, so this is the test that notices a branch that lets go of a subnet and forgets the route.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ClusterIpv6RouteConsistencyDbTests(PostgresFixture pg)
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
            new NetworkRepository(db), new NetworkRoutes(new ClusterIpv6RouteRepository(db)), new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller), ClusterWithIpv6(clusterId));
        CreateNetworkCommandResult result = await handler.HandleAsync(
            new CreateNetworkCommand(name, clusterId, EnablePublicIpv6: true));
        return result.NetworkId;
    }

    private async Task<Guid> WaitingRequestAsync(Guid networkId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await new NetworkRepository(db).FindByIdAsync(networkId))!.Operation.PendingId!.Value;
    }

    private async Task EndAsync(Guid networkId, Guid requestId, string outcome, string? externalId = null,
        string? cidr = null, string? gateway = null, string? reason = null)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var caller = Callers.TenantMember(Guid.NewGuid());
        var handler = new EndNetworkRequestCommandHandler(
            new NetworkRepository(db), new NetworkRoutes(new ClusterIpv6RouteRepository(db)),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new EndNetworkRequestCommand(networkId, requestId, outcome, externalId, null, cidr, gateway,
            reason, reason == null ? null : "raw incus error"));
    }

    private async Task<Guid> AskForDeleteAsync(Guid networkId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var repo = new NetworkRepository(db);
        Network network = (await repo.FindByIdAsync(networkId))!;
        Guid delete = network.Delete()!.OperationId;
        await repo.SaveAsync();
        return delete;
    }

    private async Task ItExistsOnIncusAsync(Guid networkId, string externalId)
    {
        await EndAsync(networkId, await WaitingRequestAsync(networkId), "Done", externalId, "10.0.0.0/24", "10.0.0.1");
        await using TitanDbContext db = pg.NewDbContext();
        await new ObserveNetworkCommandHandler(new NetworkRepository(db)).HandleAsync(new ObserveNetworkCommand(networkId, true, "Created"));
    }

    [Fact]
    public async Task EveryLiveNetworkWithASubnet_HoldsExactlyOneRoute_AndEveryHeldRouteBelongsToOne()
    {
        string cluster = $"cluster-{Guid.NewGuid():N}";
        Tenant tenant = TestTenant.Active($"route-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", maxNetworks: 10);
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
            await new ClusterIpv6RouteRepository(seed).EnsureRoutesAsync(cluster);
        }

        Guid deleted = await CreateAsync(tenant.Id, cluster, "deleted-one");
        Guid gaveUp = await CreateAsync(tenant.Id, cluster, "gave-up-one");
        Guid alive = await CreateAsync(tenant.Id, cluster, "alive-one");
        Guid keptAfterGiveUp = await CreateAsync(tenant.Id, cluster, "kept-one");

        await ItExistsOnIncusAsync(deleted, "net-deleted");
        Guid delete = await AskForDeleteAsync(deleted);
        await EndAsync(deleted, delete, "Done");
        await EndAsync(gaveUp, await WaitingRequestAsync(gaveUp), "Failed", reason: "Network creation failed.");
        await EndAsync(keptAfterGiveUp, await WaitingRequestAsync(keptAfterGiveUp), "Failed",
            "net-kept", "10.1.0.0/24", "10.1.0.1", "Network creation failed.");
        await EndAsync(deleted, delete, "Done"); // a repeated report

        await using TitanDbContext read = pg.NewDbContext();
        List<Guid> liveWithASubnet = await read.Networks
            .Where(n => n.ClusterId == cluster && n.Status != NetworkStatus.Deleted && n.PublicIpv6Subnet != null)
            .Select(n => n.Id)
            .ToListAsync();
        List<ClusterIpv6Route> routes = await read.ClusterIpv6Routes.Where(r => r.ClusterId == cluster).ToListAsync();
        List<Guid> holders = routes.Where(r => !r.IsFree).Select(r => r.NetworkId!.Value).ToList();

        Assert.Equal(liveWithASubnet.OrderBy(id => id), holders.OrderBy(id => id));
        Assert.Equal(new[] { alive, keptAfterGiveUp }.OrderBy(id => id), holders.OrderBy(id => id));
        // The two routes that went back carry the time they went back; the next network gets a route never used.
        Assert.Equal(2, routes.Count(r => r.IsFree && r.ReleasedAt != null));
        Assert.Equal(5, await CreateNumberAsync(tenant.Id, cluster));
    }

    private async Task<int> CreateNumberAsync(Guid tenantId, string cluster)
    {
        Guid next = await CreateAsync(tenantId, cluster, "next-one");
        await using TitanDbContext read = pg.NewDbContext();
        ClusterIpv6Route route = (await new ClusterIpv6RouteRepository(read).FindHeldByAsync(next))!;
        return route.Number;
    }
}

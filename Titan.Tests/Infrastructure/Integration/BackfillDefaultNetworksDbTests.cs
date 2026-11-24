using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Networking.Network;
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

/// The backfill job BackfillDefaultNetworksCommandHandler (titan-ops features/network/design.md#default-network):
/// A pass gives every tenant Active and never-had-one its default network and raises a MaxNetworks still at 1
/// to 2; a tenant with a network of its own gets no second one; a Pending tenant is untouched; a second pass changes
/// nothing more. A tenant whose default network it once had, now Deleted, does not get another.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class BackfillDefaultNetworksDbTests(PostgresFixture pg)
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

    private async Task RunAsync(string clusterId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var tenants = new TenantRepository(db);
        var defaultNetwork = new DefaultNetwork(
            new NetworkRepository(db), new NetworkRoutes(new ClusterIpv6RouteRepository(db)), ClusterWithIpv6(clusterId),
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), Callers.TenantMember(Guid.NewGuid())),
            NullLogger<DefaultNetwork>.Instance);
        var handler = new BackfillDefaultNetworksCommandHandler(tenants, new PostgresJobPassLock(db), defaultNetwork);
        await handler.HandleAsync(new BackfillDefaultNetworksCommand());
    }

    [Fact]
    public async Task APass_GivesEveryNeverHadOneTenant_ADefaultNetwork_RaisesMaxNetworksFrom1_AndASecondPassAddsNothingMore()
    {
        string cluster = "cluster-" + Guid.NewGuid().ToString("N")[..12];
        Tenant neverHad = TestTenant.Active("backfill-never-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid(), maxNetworks: 1);
        Tenant alreadyHas = TestTenant.Active("backfill-has-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid(), maxNetworks: 1);
        Tenant pending = Tenant.CreatePending("backfill-pending-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid(), maxNetworks: 1);

        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.AddRange(neverHad, alreadyHas, pending);
            await seed.SaveChangesAsync();
            await new ClusterIpv6RouteRepository(seed).EnsureRoutesAsync(cluster);
            Network own = Network.Create(alreadyHas.Id, NetworkName.Parse("own"), cluster, null, wantsPublicIpv6: false);
            seed.Networks.Add(own);
            await seed.SaveChangesAsync();
        }

        await RunAsync(cluster);
        await RunAsync(cluster);

        await using TitanDbContext read = pg.NewDbContext();
        List<Network> neverHadNetworks = await read.Networks.Where(n => n.TenantId == neverHad.Id).ToListAsync();
        Network onlyDefault = Assert.Single(neverHadNetworks);
        Assert.Equal("default", onlyDefault.Name.Value);
        Assert.Equal(2, (await read.Tenants.SingleAsync(t => t.Id == neverHad.Id)).Quota.MaxNetworks);

        List<Network> alreadyHasNetworks = await read.Networks.Where(n => n.TenantId == alreadyHas.Id).ToListAsync();
        Network onlyOwn = Assert.Single(alreadyHasNetworks);
        Assert.Equal("own", onlyOwn.Name.Value);
        Assert.Equal(2, (await read.Tenants.SingleAsync(t => t.Id == alreadyHas.Id)).Quota.MaxNetworks);

        Assert.Equal(0, await read.Networks.CountAsync(n => n.TenantId == pending.Id));
        Tenant pendingAfter = await read.Tenants.SingleAsync(t => t.Id == pending.Id);
        Assert.Equal(TenantStatus.PendingApproval, pendingAfter.Status);
        Assert.Equal(1, pendingAfter.Quota.MaxNetworks);
    }

    [Fact]
    public async Task ATenantWhoseDefaultNetworkIsDeleted_DoesNotGetAnotherOne()
    {
        string cluster = "cluster-" + Guid.NewGuid().ToString("N")[..12];
        Tenant tenant = TestTenant.Active("backfill-del-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid(), maxNetworks: 1);

        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
            await new ClusterIpv6RouteRepository(seed).EnsureRoutesAsync(cluster);
            // Reaches Deleted the way the create and delete reports would, without holding a public IPv6 /64, so
            // Delete() needs no route bookkeeping (titan-ops architecture/desired-and-current.md#delete-locally).
            Network deletedDefault = Network.Create(tenant.Id, NetworkName.Parse("default"), cluster, null, wantsPublicIpv6: false);
            deletedDefault.CreateSucceeded(deletedDefault.Operation.PendingId!.Value, "net-ext", NetworkStatus.Created, null, null);
            OperationTicket delete = deletedDefault.Delete()!;
            deletedDefault.DeleteSucceeded(delete.OperationId);
            seed.Networks.Add(deletedDefault);
            await seed.SaveChangesAsync();
        }

        await RunAsync(cluster);

        await using TitanDbContext read = pg.NewDbContext();
        List<Network> networks = await read.Networks.Where(n => n.TenantId == tenant.Id).ToListAsync();
        Network only = Assert.Single(networks);
        Assert.Equal(NetworkStatus.Deleted, only.Status);
    }
}

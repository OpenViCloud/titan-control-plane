using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Networking.Network;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.ProviderCommand;
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
/// nothing more. A tenant whose default network it once had, now Deleted, does not get another. A default network made
/// before it opened TCP 80 and 443 gets the ones it never had: not TCP 22, not one its owner deleted, nothing without room
/// for all of them.
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
        var networks = new NetworkRepository(db);
        var activity = new ActivityRecorder(new ActivityEventRepository(db), Callers.TenantMember(Guid.NewGuid()));
        var commands = new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System);
        var handler = new BackfillDefaultNetworksCommandHandler(tenants, networks, new PostgresJobPassLock(db), defaultNetwork,
            commands, activity, NullLogger<BackfillDefaultNetworksCommandHandler>.Instance);
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

    // A default network made when only TCP 22 opened: one SSH rule left, and a TCP 80 rule from ::/0 its owner added and
    // later deleted, the row kept as Deleted.
    private static Network CreatedDefaultNetworkWithOneSshRule_AndADeletedWebRule(Tenant tenant, string cluster)
    {
        Network network = Network.Create(tenant.Id, NetworkName.Parse("default"), cluster, null, wantsPublicIpv6: false,
            opensSshWhenCreated: true);
        network.CreateSucceeded(network.Operation.PendingId!.Value, "net-" + Guid.NewGuid().ToString("N")[..8],
            NetworkStatus.Created, null, null);
        network.AddFirewallRule(Guid.NewGuid(), FirewallDirection.Ingress, FirewallProtocol.TCP, PortRange.Of(22, 22),
            FirewallCidr.Parse("::/0"));
        Guid closed = Guid.NewGuid();
        OperationTicket create = network.AddFirewallRule(closed, FirewallDirection.Ingress, FirewallProtocol.TCP,
            PortRange.Of(80, 80), FirewallCidr.Parse("::/0"));
        network.FirewallRuleCreateSucceeded(closed, create.OperationId, "rule-80", FirewallRuleStatus.enabled);
        OperationTicket delete = network.DeleteFirewallRule(closed)!;
        network.FirewallRuleDeleteSucceeded(closed, delete.OperationId);
        return network;
    }

    private static async Task<List<string>> RulesOfAsync(TitanDbContext db, Guid networkId) =>
        (await db.FirewallRules.Where(r => r.NetworkId == networkId).ToListAsync())
        .Select(r => $"{r.Direction} {r.Protocol} {r.StartPort}-{r.EndPort} {r.Cidr.Value} {r.Status}")
        .OrderBy(s => s, StringComparer.Ordinal)
        .ToList();

    [Fact]
    public async Task ADefaultNetworkMadeBeforeTheWebPorts_GetsTheWebRulesItNeverHad_NotTcp22_NorOneItsOwnerDeleted_AndASecondPassAddsNothing()
    {
        string cluster = "cluster-" + Guid.NewGuid().ToString("N")[..12];
        Tenant tenant = TestTenant.Active("backfill-web-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid());
        Network defaultNetwork = CreatedDefaultNetworkWithOneSshRule_AndADeletedWebRule(tenant, cluster);
        Network own = Network.Create(tenant.Id, NetworkName.Parse("own"), cluster, null, wantsPublicIpv6: false);
        own.CreateSucceeded(own.Operation.PendingId!.Value, "net-own", NetworkStatus.Created, null, null);

        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
            await new ClusterIpv6RouteRepository(seed).EnsureRoutesAsync(cluster);
            seed.Networks.AddRange(defaultNetwork, own);
            await seed.SaveChangesAsync();
        }

        await RunAsync(cluster);
        await RunAsync(cluster);

        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(
            ["Ingress TCP 22-22 ::/0 Creating", "Ingress TCP 443-443 0.0.0.0/0 Creating", "Ingress TCP 443-443 ::/0 Creating",
                "Ingress TCP 80-80 0.0.0.0/0 Creating", "Ingress TCP 80-80 ::/0 Deleted"],
            await RulesOfAsync(read, defaultNetwork.Id));
        Assert.Empty(await RulesOfAsync(read, own.Id));
        Assert.Equal(3, await read.ProviderCommands.CountAsync(c =>
            c.TenantId == tenant.Id && c.Kind == ProviderCommandKinds.FirewallRuleCreate));
    }

    [Fact]
    public async Task ADefaultNetworkWithoutRoomForEveryWebRuleItNeverHad_GetsNone()
    {
        string cluster = "cluster-" + Guid.NewGuid().ToString("N")[..12];
        Tenant tenant = TestTenant.Active("backfill-full-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid());
        tenant.UpdateQuota(TenantQuota.Create(
            maxVms: tenant.Quota.MaxVms, maxNetworks: tenant.Quota.MaxNetworks, maxVolumes: tenant.Quota.MaxVolumes,
            maxVmSnapshotsPerVm: tenant.Quota.MaxVmSnapshotsPerVm,
            maxVolumeSnapshotsPerVolume: tenant.Quota.MaxVolumeSnapshotsPerVolume,
            maxFirewallRulesPerNetwork: 3, maxPublicIpv6: tenant.Quota.MaxPublicIpv6,
            maxSshKeys: tenant.Quota.MaxSshKeys, maxLoadBalancers: tenant.Quota.MaxLoadBalancers));
        Network defaultNetwork = CreatedDefaultNetworkWithOneSshRule_AndADeletedWebRule(tenant, cluster);

        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
            await new ClusterIpv6RouteRepository(seed).EnsureRoutesAsync(cluster);
            seed.Networks.Add(defaultNetwork);
            await seed.SaveChangesAsync();
        }

        await RunAsync(cluster);

        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(["Ingress TCP 22-22 ::/0 Creating", "Ingress TCP 80-80 ::/0 Deleted"], await RulesOfAsync(read, defaultNetwork.Id));
    }
}

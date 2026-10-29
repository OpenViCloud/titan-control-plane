using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Compute.LoadBalancer;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Infrastructure.Endpoints;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres shows for load balancers (titan-ops features/load-balancer/design.md#lb-data-model): the VIP index
/// between two creates, the child rows a request leaves and an observation drops, and the queries the VM and network
/// deletes read. Category=Integration — its own CI step; needs real Postgres; no doubles
/// (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class LoadBalancerDbTests(PostgresFixture pg)
{
    private const string Subnet = "2001:41d0:d00:d405::/64";
    private static readonly LoadBalancerListenerSpec Http = new(LoadBalancerProtocol.TCP, 80, 8080);
    private static readonly LoadBalancerListenerSpec Dns = new(LoadBalancerProtocol.UDP, 53, 53);

    private static Vm VmOnIncus(Tenant tenant, Guid networkId)
    {
        var vm = Vm.CreateUnchecked(tenant.Id, networkId, $"vm-{Guid.NewGuid():N}"[..12], "m", "tpl", "cluster-1", 20);
        vm.MarkCreated($"vm{Guid.NewGuid():N}"[..28]);
        vm.MarkObserved(VmState.Running, "10.0.0.5", null);
        return vm;
    }

    private static LoadBalancer NewLoadBalancer(Tenant tenant, Guid networkId, int vipIndex, params Vm[] backends) =>
        LoadBalancer.Create(tenant, 0, $"lb-{Guid.NewGuid():N}"[..12], networkId, "net-ext", Subnet, "cluster-1",
            LoadBalancerVip.For(Subnet, vipIndex), [Http], backends);

    private async Task SaveAsync(LoadBalancer lb)
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.LoadBalancers.Add(lb);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TwoLiveLoadBalancersOfANetwork_OnTheSameVip_TheSecondIs409VipConflict()
    {
        var tenant = TestTenant.Active("a", "kc-a", "a@a.com");
        Guid networkId = Guid.NewGuid();
        Vm vm = VmOnIncus(tenant, networkId);
        await SaveAsync(NewLoadBalancer(tenant, networkId, 1, vm));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(NewLoadBalancer(tenant, networkId, 1, vm)));

        Assert.Equal(new ErrorStatus(409, ErrorStatus.LoadBalancerVipConflictCode), ErrorStatus.For(ex));
    }

    [Fact]
    public async Task ADeletedLoadBalancersVip_IsFreeAgain_InTheIndexAndTheQuery()
    {
        var tenant = TestTenant.Active("a", "kc-a", "a@a.com");
        Guid networkId = Guid.NewGuid();
        Vm vm = VmOnIncus(tenant, networkId);
        LoadBalancer gone = NewLoadBalancer(tenant, networkId, 1, vm);
        gone.MarkCreated(gone.Vip);
        gone.RequestDelete();
        gone.MarkDeleted();
        await SaveAsync(gone);
        await SaveAsync(NewLoadBalancer(tenant, networkId, 2, vm));

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Assert.Equal(new HashSet<int> { 2 }, await new LoadBalancerRepository(db).ListVipIndexesOfNetworkAsync(networkId));
        }
        await SaveAsync(NewLoadBalancer(tenant, networkId, 1, vm));
    }

    // The rows an update leaves while it waits (asked and applied side by side) survive a round trip, and the ones the
    // Updated report drops are gone from the table, not only from the collection.
    [Fact]
    public async Task AnUpdatesRows_RoundTrip_AndTheAppliedReportDropsTheOldOnes()
    {
        var tenant = TestTenant.Active("a", "kc-a", "a@a.com");
        Guid networkId = Guid.NewGuid();
        Vm a = VmOnIncus(tenant, networkId);
        Vm b = VmOnIncus(tenant, networkId);
        LoadBalancer lb = NewLoadBalancer(tenant, networkId, 1, a);
        lb.MarkCreated(lb.Vip);
        await SaveAsync(lb);

        Guid intent;
        await using (TitanDbContext db = pg.NewDbContext())
        {
            LoadBalancer loaded = (await new LoadBalancerRepository(db).FindByIdAsync(lb.Id))!;
            intent = loaded.RequestUpdate([Dns], [b]);
            await db.SaveChangesAsync();
        }
        await using (TitanDbContext db = pg.NewDbContext())
        {
            LoadBalancer waiting = (await new LoadBalancerRepository(db).FindByIdAsync(lb.Id))!;
            Assert.Equal(new[] { 80 }, waiting.AppliedListeners.Select(l => l.Port));
            Assert.Equal(new[] { 53 }, waiting.DesiredListeners.Select(l => l.Port));
            Assert.Equal(new[] { b.Id }, waiting.DesiredBackends.Select(x => x.VmId));
            Assert.True(waiting.MarkUpdated(intent));
            await db.SaveChangesAsync();
        }
        await using (TitanDbContext db = pg.NewDbContext())
        {
            Assert.Equal(1, await db.Set<LoadBalancerListener>().CountAsync(l => l.LoadBalancerId == lb.Id));
            Assert.Equal(1, await db.Set<LoadBalancerBackend>().CountAsync(x => x.LoadBalancerId == lb.Id));
            Assert.False(await new LoadBalancerRepository(db).IsBackendAsync(a.Id));
            Assert.True(await new LoadBalancerRepository(db).IsBackendAsync(b.Id));
        }
    }

    // The VM delete refuses while a live load balancer sends to the VM; once the load balancer is Deleted, it does not.
    [Fact]
    public async Task IsBackend_AndHasLiveLoadBalancers_IgnoreDeletedLoadBalancers()
    {
        var tenant = TestTenant.Active("a", "kc-a", "a@a.com");
        Guid networkId = Guid.NewGuid();
        Vm vm = VmOnIncus(tenant, networkId);
        LoadBalancer lb = NewLoadBalancer(tenant, networkId, 1, vm);
        await SaveAsync(lb);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Assert.True(await new LoadBalancerRepository(db).IsBackendAsync(vm.Id));
            Assert.True(await new NetworkRepository(db).HasLiveLoadBalancersAsync(networkId));
            LoadBalancer loaded = (await new LoadBalancerRepository(db).FindByIdAsync(lb.Id))!;
            loaded.MarkCreated(loaded.Vip);
            loaded.RequestDelete();
            loaded.MarkDeleted();
            await db.SaveChangesAsync();
        }
        await using (TitanDbContext db = pg.NewDbContext())
        {
            Assert.False(await new LoadBalancerRepository(db).IsBackendAsync(vm.Id));
            Assert.False(await new NetworkRepository(db).HasLiveLoadBalancersAsync(networkId));
        }
    }
}

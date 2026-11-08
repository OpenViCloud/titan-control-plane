using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The load balancer queries that hide only a deleted one (!LoadBalancer.IsDeleted): the tenant list, the name, the
// VIP numbers a network has given out, and whether a VM is still a backend
// (titan-ops features/load-balancer/design.md). One row per shape, so a hidden row cannot be covered by another.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it); quota is
// QueryRuleDbTests.LoadBalancerQuota_*.
public class LoadBalancerRepositoryStateTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid Tenant = TenantOwner.Id;
    private static readonly Guid NetworkId = Guid.NewGuid();
    private const string Subnet = "2001:41d0:d00:d405::/64";

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"load-balancer-state-{Guid.NewGuid()}")
            .Options);

    private static Vm Backend(string name)
    {
        Vm vm = Vm.Create(TenantOwner.Id, NetworkId, name, "m", "tpl-1", "vn-hcm-1", 20);
        vm.MarkRunning($"ext-{name}", "10.0.0.5");
        return vm;
    }

    private static LoadBalancer Creating(string name, int vipIndex, Vm backend) =>
        LoadBalancer.Create(TenantOwner.Id, name, NetworkId, "net-ext", Subnet, "vn-hcm-1",
            LoadBalancerVip.For(Subnet, vipIndex), [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [backend]);

    private static LoadBalancer Created(string name, int vipIndex, Vm backend)
    {
        LoadBalancer loadBalancer = Creating(name, vipIndex, backend);
        loadBalancer.MarkCreated(loadBalancer.Vip);
        return loadBalancer;
    }

    private static LoadBalancer Updating(string name, int vipIndex, Vm backend)
    {
        LoadBalancer loadBalancer = Created(name, vipIndex, backend);
        loadBalancer.Update([new LoadBalancerListenerSpec(LoadBalancerProtocol.UDP, 53, 53)], [backend]);
        return loadBalancer;
    }

    // Every shape, named; the name is what the assertions list. Each has its own backend VM, so IsBackendAsync is
    // asked of one load balancer at a time.
    private static (List<LoadBalancer> LoadBalancers, Dictionary<string, Guid> BackendOf) EveryShape()
    {
        var backendOf = new Dictionary<string, Guid>();
        LoadBalancer Shape(string name, int vipIndex, Func<string, int, Vm, LoadBalancer> make)
        {
            Vm backend = Backend($"vm-{name}");
            backendOf[name] = backend.Id;
            return make(name, vipIndex, backend);
        }

        LoadBalancer deleting = Shape("deleting", 3, Created);
        deleting.Delete();
        LoadBalancer deleted = Shape("deleted", 4, Created);
        deleted.Delete();
        deleted.MarkDeleted();
        LoadBalancer notFound = Shape("not-found", 5, Created);
        notFound.MarkNotFound();
        LoadBalancer createFailed = Shape("create-failed", 6, Creating);
        createFailed.MarkRequestFailed("refused", null);

        LoadBalancer updatingSeen = Shape("updating-seen", 7, Updating);
        LoadBalancer updatingLost = Shape("updating-lost", 8, Updating);
        updatingLost.MarkNotFound();
        LoadBalancer deletingLost = Shape("deleting-lost", 9, Created);
        deletingLost.MarkNotFound();
        deletingLost.Delete();

        return ([Shape("creating", 1, Creating), Shape("created", 2, Created), deleting, deleted, notFound, createFailed,
            updatingSeen, updatingLost, deletingLost], backendOf);
    }

    private static async Task<(LoadBalancerRepository Repo, Dictionary<string, Guid> BackendOf)> SeededAsync(TitanDbContext db)
    {
        (List<LoadBalancer> loadBalancers, Dictionary<string, Guid> backendOf) = EveryShape();
        db.LoadBalancers.AddRange(loadBalancers);
        await db.SaveChangesAsync();
        return (new LoadBalancerRepository(db), backendOf);
    }

    [Fact]
    public async Task List_HidesOnlyTheDeleted()
    {
        using var db = NewDb();
        (LoadBalancerRepository repo, _) = await SeededAsync(db);

        List<LoadBalancer> listed = await repo.ListByTenantAsync(Tenant);

        Assert.Equal(new[] { "create-failed", "created", "creating", "deleting", "deleting-lost", "not-found", "updating-lost", "updating-seen" },
            listed.Select(lb => lb.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task Name_IsTakenByEveryLoadBalancerButADeletedOne()
    {
        using var db = NewDb();
        (LoadBalancerRepository repo, Dictionary<string, Guid> backendOf) = await SeededAsync(db);

        foreach (string name in backendOf.Keys)
        {
            bool expectTaken = name != "deleted";
            Assert.True(expectTaken == await repo.ExistsByNameAsync(Tenant, name), name);
        }
    }

    [Fact]
    public async Task VipNumbers_StayGivenOut_UntilTheLoadBalancerIsDeleted()
    {
        using var db = NewDb();
        (LoadBalancerRepository repo, _) = await SeededAsync(db);

        HashSet<int> taken = await repo.ListVipIndexesOfNetworkAsync(NetworkId);

        Assert.Equal(new[] { 1, 2, 3, 5, 6, 7, 8, 9 }, taken.OrderBy(i => i).ToArray()); // 4 is the deleted one
    }

    [Fact]
    public async Task AVm_IsABackend_OfEveryLoadBalancerButADeletedOne()
    {
        using var db = NewDb();
        (LoadBalancerRepository repo, Dictionary<string, Guid> backendOf) = await SeededAsync(db);

        foreach ((string name, Guid vmId) in backendOf)
        {
            bool expectBackend = name != "deleted";
            Assert.True(expectBackend == await repo.IsBackendAsync(vmId), name);
        }
    }

    // The quota counts what Incus holds or is about to: an update counts only while Incus was seen holding the load
    // balancer, a delete in flight and a load balancer Incus lost not at all.
    [Fact]
    public async Task Quota_CountsCreatingAvailableAndAnUpdateIncusStillHolds()
    {
        using var db = NewDb();
        (LoadBalancerRepository repo, _) = await SeededAsync(db);

        Assert.Equal(3, await repo.CountActiveByTenantAsync(Tenant));
        List<LoadBalancer> counted = EveryShape().LoadBalancers.Where(lb => lb.OccupiesQuota).ToList();
        Assert.Equal(new[] { "created", "creating", "updating-seen" }, counted.Select(lb => lb.Name).OrderBy(n => n).ToArray());
    }

    // The repository writes each condition in LINQ; the rule is the plain bool on LoadBalancer
    // (titan-ops architecture/domain-methods.md#plain-code). Every shape through both.
    [Fact]
    public async Task EveryQuery_AgreesWithTheBoolOnTheLoadBalancer_ForEveryShape()
    {
        using var db = NewDb();
        (List<LoadBalancer> shapes, Dictionary<string, Guid> backendOf) = EveryShape();
        db.LoadBalancers.AddRange(shapes);
        await db.SaveChangesAsync();
        var repo = new LoadBalancerRepository(db);
        string[] ByName(IEnumerable<LoadBalancer> rows) => rows.Select(lb => lb.Name).OrderBy(n => n).ToArray();

        Assert.Equal(shapes.Count(lb => lb.OccupiesQuota), await repo.CountActiveByTenantAsync(Tenant));
        Assert.Equal(ByName(shapes.Where(lb => !lb.IsDeleted)), ByName(await repo.ListByTenantAsync(Tenant)));
        Assert.Equal(ByName(shapes.Where(lb => !lb.IsDeleted && lb.ExternalId != null)), ByName(await repo.ListReconcilableAsync("vn-hcm-1")));
        Assert.Equal(shapes.Where(lb => !lb.IsDeleted).Select(lb => lb.VipIndex).OrderBy(i => i), (await repo.ListVipIndexesOfNetworkAsync(NetworkId)).OrderBy(i => i));
        foreach (LoadBalancer loadBalancer in shapes)
        {
            Assert.True(!loadBalancer.IsDeleted == await repo.ExistsByNameAsync(Tenant, loadBalancer.Name), loadBalancer.Name);
            Assert.True(!loadBalancer.IsDeleted == await repo.IsBackendAsync(backendOf[loadBalancer.Name]), loadBalancer.Name);
        }
    }
}

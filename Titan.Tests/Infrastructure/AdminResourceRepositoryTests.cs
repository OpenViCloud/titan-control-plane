using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read — the cross-tenant read query itself, against a real (InMemory) DbContext.
// This is where the security-relevant behavior lives: ListAllAsync spans tenants (unlike the
// scoped ListByTenantAsync it sits beside and must NOT replace), narrows to one tenant when asked,
// and still hides terminal rows (Destroyed VMs / Deleted networks) so an admin sees live inventory,
// not expunged garbage. Isolation of the tenant-facing path is a separate invariant,
// pinned by VmServiceTests/NetworkServiceTests.
public class AdminResourceRepositoryTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantAOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantA = TenantAOwner.Id;
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantBOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantB = TenantBOwner.Id;

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"admin-read-{Guid.NewGuid()}")
            .Options);

    private static Vm Vm(Titan.Domain.Aggregates.Tenant.Tenant owner, string name, Action<Vm>? mutate = null)
    {
        var vm = Titan.Domain.Aggregates.Vm.Vm.Create(owner.Id, Guid.NewGuid(), name, "s", "t", "vn-hcm-1", 20);
        mutate?.Invoke(vm);
        return vm;
    }

    private static Network Net(Titan.Domain.Aggregates.Tenant.Tenant owner, string name, Action<Network>? mutate = null)
    {
        var net = TestAggregates.Network(owner, name, "vn-hcm-1");
        mutate?.Invoke(net);
        return net;
    }

    // ── VMs ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VmListAll_NullFilter_ReturnsVmsAcrossAllTenants()
    {
        using var db = NewDb();
        db.Vms.AddRange(Vm(TenantAOwner, "a1"), Vm(TenantBOwner, "b1"));
        await db.SaveChangesAsync();
        var repo = new VmRepository(db);

        var all = await repo.ListAllAsync(null);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, v => v.TenantId == TenantA);
        Assert.Contains(all, v => v.TenantId == TenantB); // cross-tenant — the whole point
    }

    [Fact]
    public async Task VmListAll_WithTenantFilter_ReturnsOnlyThatTenant()
    {
        using var db = NewDb();
        db.Vms.AddRange(Vm(TenantAOwner, "a1"), Vm(TenantAOwner, "a2"), Vm(TenantBOwner, "b1"));
        await db.SaveChangesAsync();
        var repo = new VmRepository(db);

        var only = await repo.ListAllAsync(TenantA);

        Assert.Equal(2, only.Count);
        Assert.All(only, v => Assert.Equal(TenantA, v.TenantId));
    }

    [Fact]
    public async Task VmListAll_NonexistentTenant_ReturnsEmpty_NotError()
    {
        using var db = NewDb();
        db.Vms.Add(Vm(TenantAOwner, "a1"));
        await db.SaveChangesAsync();
        var repo = new VmRepository(db);

        Assert.Empty(await repo.ListAllAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task VmListAll_ExcludesDeleted()
    {
        using var db = NewDb();
        db.Vms.AddRange(
            Vm(TenantAOwner, "live"),
            Vm(TenantBOwner, "gone", v => { v.MarkCreated("gone-1"); v.RequestDelete(); v.MarkDeleted(); })); // expunged — must not surface to admin
        await db.SaveChangesAsync();
        var repo = new VmRepository(db);

        var all = await repo.ListAllAsync(null);

        var vm = Assert.Single(all);
        Assert.Equal("live", vm.Name);
    }

    // ── Networks ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkListAll_NullFilter_ReturnsNetworksAcrossAllTenants()
    {
        using var db = NewDb();
        db.Networks.AddRange(Net(TenantAOwner, "a-net"), Net(TenantBOwner, "b-net"));
        await db.SaveChangesAsync();
        var repo = new NetworkRepository(db);

        var all = await repo.ListAllAsync(null);

        Assert.Equal(2, all.Count);
        Assert.Contains(all, n => n.TenantId == TenantA);
        Assert.Contains(all, n => n.TenantId == TenantB);
    }

    [Fact]
    public async Task NetworkListAll_WithTenantFilter_ReturnsOnlyThatTenant()
    {
        using var db = NewDb();
        db.Networks.AddRange(Net(TenantAOwner, "a-net"), Net(TenantBOwner, "b-net"));
        await db.SaveChangesAsync();
        var repo = new NetworkRepository(db);

        var only = await repo.ListAllAsync(TenantB);

        var net = Assert.Single(only);
        Assert.Equal(TenantB, net.TenantId);
    }

    [Fact]
    public async Task NetworkListAll_ExcludesDeleted()
    {
        using var db = NewDb();
        db.Networks.AddRange(
            Net(TenantAOwner, "live", n => { n.MarkActive("e", "10.0.0.0/24", "10.0.0.1"); }),
            Net(TenantBOwner, "gone", n => { n.MarkActive("e", "10.0.1.0/24", "10.0.1.1"); n.RequestDelete(); n.MarkDeleted(); }));
        await db.SaveChangesAsync();
        var repo = new NetworkRepository(db);

        var all = await repo.ListAllAsync(null);

        var net = Assert.Single(all);
        Assert.Equal("live", net.Name.Value);
    }
}

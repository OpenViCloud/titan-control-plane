using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The VM queries that decide billing, quota, the name and whether a network is in use
// (titan-ops features/vm/design.md#counting-and-billing), one row per shape a VM can be in.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it); the
// unique index itself is VmNameUniquenessDbTests.
public class VmRepositoryStateTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid Tenant = TenantOwner.Id;
    private static readonly Guid NetworkId = Guid.NewGuid();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"vm-state-{Guid.NewGuid()}")
            .Options);

    // Every shape, named; the name is what the assertions list.
    private static List<Vm> EveryShape()
    {
        Vm New(string name) => Vm.Create(TenantOwner.Id, NetworkId, name, "m", "tpl-1", "cluster-a", 20);
        Vm Seen(string name, VmStatus state)
        {
            Vm vm = New(name);
            vm.MarkRunning($"ext-{name}", "10.0.0.5");
            vm.MarkObserved(state, null);
            return vm;
        }

        Vm creating = New("creating");
        Vm createdNotSeen = New("created-not-seen");
        createdNotSeen.MarkCreated("ext-created-not-seen");
        Vm createFailed = New("create-failed");
        createFailed.MarkRequestFailed("refused", null);
        // The give-up carried the id: Incus holds an instance, which only the delete command removes.
        Vm createFailedWithAnInstance = New("create-failed-with-instance");
        createFailedWithAnInstance.MarkCreated("ext-create-failed-with-instance");
        createFailedWithAnInstance.MarkRequestFailed("refused", null);
        Vm notFound = Seen("not-found", VmStatus.Running);
        notFound.MarkNotFound();
        Vm deleting = Seen("deleting", VmStatus.Running);
        deleting.Delete(Guid.NewGuid());
        Vm deleted = Seen("deleted", VmStatus.Running);
        deleted.Delete(Guid.NewGuid());
        deleted.MarkDeleted();

        return new List<Vm>
        {
            creating, createdNotSeen, createFailed, createFailedWithAnInstance,
            Seen("running", VmStatus.Running), Seen("ready", VmStatus.Ready), Seen("stopped", VmStatus.Stopped),
            Seen("frozen", VmStatus.Frozen), Seen("error", VmStatus.Error),
            notFound, deleting, deleted
        };
    }

    private static async Task<VmRepository> SeededAsync(TitanDbContext db)
    {
        db.Vms.AddRange(EveryShape());
        await db.SaveChangesAsync();
        return new VmRepository(db);
    }

    // billed while the instance holds CPU and RAM. Stopped is the one Incus status without a qemu process;
    // a VM with no instance (not created, not found, deleted) holds nothing. A delete in flight still holds it.
    [Fact]
    public async Task Billable_IsEveryStatusWithItsQemuAlive_NeverStoppedNorAbsent()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);

        List<Vm> billable = await repo.ListBillableAsync();

        Assert.Equal(new[] { "deleting", "error", "frozen", "ready", "running" },
            billable.Select(v => v.Name).OrderBy(n => n).ToArray());
    }

    // quota counts what occupies the provider or is about to (a create waiting); a delete asked for frees
    // it at once. A failed create with no instance and an instance not found occupy nothing; a failed create whose
    // instance Incus holds occupies it until deleted.
    [Fact]
    public async Task Quota_CountsWhatExistsOrIsBeingCreated_ButNotAFailedCreate_NotFound_OrADeleteAsked()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);

        int counted = await repo.CountActiveByTenantAsync(Tenant);

        // creating, created-not-seen, create-failed-with-instance, running, ready, stopped, frozen, error
        Assert.Equal(8, counted);
    }

    [Fact] // the same condition as the unique index's filter: only a deleted VM frees its name
    public async Task Name_IsTakenByEveryVmButADeletedOne()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);

        foreach (Vm vm in EveryShape())
        {
            bool expectTaken = vm.Name != "deleted";
            Assert.True(expectTaken == await repo.ExistsByNameAsync(Tenant, vm.Name), vm.Name);
        }
    }

    [Fact] // the tenant list and the admin list hide only deleted VMs
    public async Task Lists_HideOnlyTheDeleted()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);

        List<Vm> tenantList = await repo.ListByTenantAsync(Tenant);
        List<Vm> adminList = await repo.ListAllAsync(null);

        Assert.Equal(11, tenantList.Count);
        Assert.DoesNotContain(tenantList, v => v.Name == "deleted");
        Assert.Equal(11, adminList.Count);
    }

    // A VM holds its network while its instance exists (Incus refuses to delete a network an instance uses, a
    // deleting one included) or its create waits. Each shape alone, so one holder cannot hide another.
    [Theory]
    [InlineData("creating", true)]
    [InlineData("created-not-seen", true)]
    [InlineData("running", true)]
    [InlineData("stopped", true)]
    [InlineData("frozen", true)]
    [InlineData("error", true)]
    [InlineData("deleting", true)]
    [InlineData("create-failed", false)]
    [InlineData("create-failed-with-instance", true)]
    [InlineData("not-found", false)]
    [InlineData("deleted", false)]
    public async Task Network_IsInUse_WhileAnInstanceExistsOrIsBeingCreated(string shape, bool inUse)
    {
        using var db = NewDb();
        db.Vms.Add(EveryShape().Single(v => v.Name == shape));
        await db.SaveChangesAsync();

        bool held = await new NetworkRepository(db).HasActiveVmsAsync(NetworkId);

        Assert.Equal(inUse, held);
    }

    // A create Titan gave up on is not watched (it is not brought back to life), with or without an instance;
    // it is never billed, as it has no Incus status.
    [Fact]
    // Every VM Incus may hold, a create that gave up after the instance was made included: an instance that runs is
    // billed whether or not its create ended well (titan-ops architecture/resource-lifecycle.md#reconciler).
    public async Task Reconciler_WatchesEveryVmWithAnInstance_ACreateThatGaveUpIncluded()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);

        List<Vm> watched = await repo.ListReconcilableAsync("cluster-a");

        Assert.Equal(new[] { "create-failed-with-instance", "created-not-seen", "deleting", "error", "frozen", "not-found", "ready", "running", "stopped" },
            watched.Select(v => v.Name).OrderBy(n => n).ToArray());
    }

    // A backup exports what Incus holds: every VM with an instance, a stopped one and a create that gave up after the
    // instance was made included; not one Incus lost nor one whose delete ended (titan-ops features/backup/design.md#incus-backup).
    [Fact]
    public async Task Backup_ListsEveryVmWithAnInstance_NotOneLostNorDeleted()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);

        List<Vm> held = await repo.ListWithAnInstanceAsync();

        Assert.Equal(new[] { "create-failed-with-instance", "created-not-seen", "deleting", "error", "frozen", "ready", "running", "stopped" },
            held.Select(v => v.Name).OrderBy(n => n).ToArray());
    }

    // The repository writes each condition in LINQ; the rule is the plain bool on Vm
    // (titan-ops architecture/domain-methods.md#plain-code). Every shape through both, so the two cannot drift apart.
    [Fact]
    public async Task EveryQuery_AgreesWithTheBoolOnTheVm_ForEveryShape()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);
        List<Vm> shapes = EveryShape();
        string[] ByName(IEnumerable<Vm> vms) => vms.Select(v => v.Name).OrderBy(n => n).ToArray();

        Assert.Equal(shapes.Count(v => v.OccupiesQuota), await repo.CountActiveByTenantAsync(Tenant));
        Assert.Equal(ByName(shapes.Where(v => v.IsBilled)), ByName(await repo.ListBillableAsync()));
        Assert.Equal(ByName(shapes.Where(v => !v.IsDeleted)), ByName(await repo.ListByTenantAsync(Tenant)));
        Assert.Equal(ByName(shapes.Where(v => !v.IsDeleted)), ByName(await repo.ListAllAsync(null)));
        Assert.Equal(ByName(shapes.Where(v => !v.IsDeleted && v.ExternalId != null)), ByName(await repo.ListReconcilableAsync("cluster-a")));
        Assert.Equal(ByName(shapes.Where(v => v.HasAnInstance)), ByName(await repo.ListWithAnInstanceAsync()));
        foreach (Vm vm in shapes)
        {
            Assert.True(!vm.IsDeleted == await repo.ExistsByNameAsync(Tenant, vm.Name), vm.Name);
        }
    }

    [Fact]
    public async Task NetworkIsHeld_AgreesWithHasNetwork_ForEveryShape()
    {
        foreach (Vm vm in EveryShape())
        {
            using var db = NewDb();
            db.Vms.Add(vm);
            await db.SaveChangesAsync();

            Assert.True(vm.HasNetwork == await new NetworkRepository(db).HasActiveVmsAsync(NetworkId), vm.Name);
        }
    }
}

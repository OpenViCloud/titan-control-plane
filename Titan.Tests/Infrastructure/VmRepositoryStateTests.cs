using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Compute.Vm;
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
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid NetworkId = Guid.NewGuid();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"vm-state-{Guid.NewGuid()}")
            .Options);

    // Every shape, named; the name is what the assertions list.
    private static List<Vm> EveryShape()
    {
        Vm New(string name) => Vm.CreateUnchecked(Tenant, NetworkId, name, "m", "tpl-1", "cluster-a", 20);
        Vm Seen(string name, VmState state)
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
        Vm notFound = Seen("not-found", VmState.Running);
        notFound.MarkNotFound();
        Vm deleting = Seen("deleting", VmState.Running);
        deleting.RequestDelete();
        Vm deleted = Seen("deleted", VmState.Running);
        deleted.RequestDelete();
        deleted.MarkDeleted();

        return new List<Vm>
        {
            creating, createdNotSeen, createFailed, createFailedWithAnInstance,
            Seen("running", VmState.Running), Seen("ready", VmState.Ready), Seen("stopped", VmState.Stopped),
            Seen("frozen", VmState.Frozen), Seen("error", VmState.Error),
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
    public async Task Reconciler_WatchesEveryVmWithAnInstance_ButNotACreateThatGaveUp()
    {
        using var db = NewDb();
        VmRepository repo = await SeededAsync(db);

        List<Vm> watched = await repo.ListReconcilableAsync("cluster-a");

        Assert.Equal(new[] { "created-not-seen", "deleting", "error", "frozen", "not-found", "ready", "running", "stopped" },
            watched.Select(v => v.Name).OrderBy(n => n).ToArray());
    }
}

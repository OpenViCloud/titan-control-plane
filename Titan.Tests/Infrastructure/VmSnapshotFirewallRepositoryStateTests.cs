using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmSnapshot;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The VM snapshot and firewall rule queries that decide quota, billing and what blocks a VM delete, one row per
// situation. A delete in flight stops counting toward quota the moment it is requested; a revert in flight still
// bills, a delete in flight does not.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it), and
// MarkAllDeletedByVmIdAsync (ExecuteUpdate — InMemory cannot run it; StateRoundTripDbTests does).
public class VmSnapshotFirewallRepositoryStateTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"vmsnap-fw-state-{Guid.NewGuid()}")
            .Options);

    private static Vm RunningVm(string cluster = "vn-hcm-1")
    {
        var vm = Vm.CreateUnchecked(Tenant, Guid.NewGuid(), "vm-1", "m", "tpl-1", cluster, 20);
        vm.MarkRunning("42", "10.0.0.5");
        return vm;
    }

    private static readonly Vm TheVm = RunningVm();

    private static VmSnapshot Creating(Vm vm, string name) => VmSnapshot.CreateUnchecked(vm.Id, Tenant, name);

    private static VmSnapshot Ready(Vm vm, string name)
    {
        var s = Creating(vm, name);
        s.MarkCreated($"42:{name}");
        return s;
    }

    private static VmSnapshot RevertRequested(Vm vm, string name)
    {
        var s = Ready(vm, name);
        s.Revert(TheVm);
        return s;
    }

    private static VmSnapshot DeleteRequested(Vm vm, string name)
    {
        var s = Ready(vm, name);
        s.RequestDelete();
        return s;
    }

    private static VmSnapshot Deleted(Vm vm, string name)
    {
        var s = DeleteRequested(vm, name);
        s.MarkDeleted();
        return s;
    }

    private static VmSnapshot NotFound(Vm vm, string name)
    {
        var s = Ready(vm, name);
        s.MarkNotFound();
        return s;
    }

    private static VmSnapshot CreateFailed(Vm vm, string name)
    {
        var s = Creating(vm, name);
        s.MarkRequestFailed("could not create", "raw");
        return s;
    }

    private static VmSnapshot[] EverySituation(Vm vm) =>
    [
        Creating(vm, "creating"), Ready(vm, "ready"), RevertRequested(vm, "reverting"),
        DeleteRequested(vm, "deleting"), Deleted(vm, "deleted"), NotFound(vm, "lost"), CreateFailed(vm, "failed"),
    ];

    // Occupancy (titan-ops features/vm/design.md#vm-snapshot-counting): a snapshot whose delete was requested stops counting
    // toward MaxVmSnapshotsPerVm; one Incus no longer has, or never had, does not count.
    [Fact]
    public async Task SnapshotQuota_CountsCreatingExistingAndReverting_ButNotADeleteInFlight_NotFound_NorAFailedCreate()
    {
        using var db = NewDb();
        db.VmSnapshots.AddRange(EverySituation(TheVm));
        await db.SaveChangesAsync();

        int count = await new VmSnapshotRepository(db).CountActiveByVmIdAsync(TheVm.Id);

        Assert.Equal(3, count); // creating, ready, reverting
    }

    // Q-C: billing follows what Incus holds, a revert or a delete in flight included.
    [Fact]
    public async Task SnapshotBilling_IsWhatIncusHolds_ADeleteInFlightIncluded()
    {
        using var db = NewDb();
        db.VmSnapshots.AddRange(EverySituation(TheVm));
        await db.SaveChangesAsync();

        var billed = (await new VmSnapshotRepository(db).ListBillableAsync()).Select(s => s.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "deleting", "ready", "reverting" }, billed);
    }

    // What blocks a VM delete (VM_SNAPSHOT_IN_PROGRESS): a create or a revert in flight.
    [Theory]
    [InlineData("creating", true)]
    [InlineData("reverting", true)]
    [InlineData("ready", false)]
    [InlineData("deleting", false)]
    [InlineData("deleted", false)]
    [InlineData("lost", false)]
    [InlineData("failed", false)]
    public async Task InProgress_IsACreateOrARevertInFlight(string situation, bool blocks)
    {
        using var db = NewDb();
        db.VmSnapshots.Add(EverySituation(TheVm).Single(s => s.Name == situation));
        await db.SaveChangesAsync();

        bool inProgress = await new VmSnapshotRepository(db).AnyInProgressByVmIdAsync(TheVm.Id);

        Assert.Equal(blocks, inProgress);
    }

    [Fact] // a snapshot has no cluster; it lives where its VM lives. Only what has a provider id and is not gone.
    public async Task SnapshotReconcilable_FollowsItsVmsCluster_AndIsWhatReachedTheProvider()
    {
        using var db = NewDb();
        var there = RunningVm("vn-han-1");
        db.Vms.AddRange(TheVm, there);
        db.VmSnapshots.AddRange(EverySituation(TheVm));
        db.VmSnapshots.Add(Ready(there, "elsewhere"));
        await db.SaveChangesAsync();

        var names = (await new VmSnapshotRepository(db).ListReconcilableAsync("vn-hcm-1")).Select(s => s.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "deleting", "lost", "ready", "reverting" }, names);
    }

    [Fact]
    public async Task SnapshotList_ShowsEverythingButDeleted()
    {
        using var db = NewDb();
        db.VmSnapshots.AddRange(EverySituation(TheVm));
        await db.SaveChangesAsync();

        var names = (await new VmSnapshotRepository(db).FindByVmIdAsync(TheVm.Id)).Select(s => s.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "creating", "deleting", "failed", "lost", "ready", "reverting" }, names);
    }

    // ── Firewall rules ─────────────────────────────────────────────────────

    // Rules are seeded through their network: they are its child entities (titan-ops architecture/code-structure.md#child-entity),
    // and the foreign key refuses a rule with no network on Postgres.
    private static FirewallRule Rule(Network network, string cidr) =>
        network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, cidr);

    private static Network UsableNetwork(string name, string cluster)
    {
        var network = Network.CreateUnchecked(Tenant, name, cluster);
        network.MarkActive($"ext-{name}", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    private static Network NetworkWithEveryRuleSituation(string cluster = "vn-hcm-1")
    {
        var network = UsableNetwork("here", cluster);
        Rule(network, "10.0.0.1/32"); // creating
        var active = Rule(network, "10.0.0.2/32");
        active.MarkActive("sg-2");
        var deleting = Rule(network, "10.0.0.3/32");
        deleting.MarkActive("sg-3");
        deleting.RequestDelete();
        var deleted = Rule(network, "10.0.0.4/32");
        deleted.MarkActive("sg-4");
        deleted.RequestDelete();
        deleted.MarkDeleted();
        var failedCreate = Rule(network, "10.0.0.5/32");
        failedCreate.MarkRequestFailed("reason", "detail");
        return network;
    }

    [Fact] // the provider still enforces a rule whose delete is waiting, so it is still listed
    public async Task RuleList_ShowsEverythingButDeleted()
    {
        using var db = NewDb();
        var network = NetworkWithEveryRuleSituation();
        db.Networks.Add(network);
        await db.SaveChangesAsync();

        var cidrs = (await new FirewallRuleRepository(db).ListByNetworkAsync(network.Id)).Select(r => r.Cidr).OrderBy(c => c).ToList();

        Assert.Equal(new[] { "10.0.0.1/32", "10.0.0.2/32", "10.0.0.3/32", "10.0.0.5/32" }, cidrs);
    }

    [Fact] // a rule has no cluster; it lives where its network lives. Only what has a provider id and is not gone.
    public async Task RuleReconcilable_FollowsItsNetworksCluster_AndIsWhatReachedTheProvider()
    {
        using var db = NewDb();
        var here = NetworkWithEveryRuleSituation("vn-hcm-1");
        var there = UsableNetwork("there", "vn-han-1");
        Rule(there, "10.9.9.9/32").MarkActive("sg-9");
        db.Networks.AddRange(here, there);
        await db.SaveChangesAsync();

        var ids = (await new FirewallRuleRepository(db).ListReconcilableAsync("vn-hcm-1")).Select(r => r.ExternalId).OrderBy(x => x).ToList();

        Assert.Equal(new[] { "sg-2", "sg-3" }, ids);
    }

    // RULES-ON-NETWORK-DELETE: the network's delete takes the ACL with it, so its rules are not observed meanwhile —
    // the reconciler would report every one not found halfway through the delete.
    [Fact]
    public async Task RuleReconcilable_LeavesOutTheRulesOfANetworkBeingDeleted()
    {
        using var db = NewDb();
        var leaving = UsableNetwork("leaving", "vn-hcm-1");
        Rule(leaving, "10.0.0.9/32").MarkActive("sg-9");
        leaving.RequestDelete();
        db.Networks.Add(leaving);
        await db.SaveChangesAsync();

        Assert.Empty(await new FirewallRuleRepository(db).ListReconcilableAsync("vn-hcm-1"));
    }
}

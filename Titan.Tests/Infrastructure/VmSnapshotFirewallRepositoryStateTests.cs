using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The VM snapshot and firewall rule read-side queries: the customer's list, billing and the reconciler's pool, one row
// per situation. Quota and "what blocks a VM delete" are the VM's own count now (VmSnapshotChildTests): a snapshot is
// the VM's child entity (titan-ops architecture/code-structure.md#child-entity), seeded through it. A revert in flight
// still bills, a delete in flight too, until Incus deleted it.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it).
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

    private static VmSnapshot Ready(Vm vm, string name)
    {
        var s = vm.AddSnapshotUnchecked(name);
        s.MarkCreated($"42:{name}");
        return s;
    }

    // A VM holding one snapshot in every situation: creating, ready, reverting, deleting, deleted, lost, failed.
    private static Vm VmWithEverySnapshotSituation(string cluster = "vn-hcm-1")
    {
        var vm = RunningVm(cluster);
        vm.AddSnapshotUnchecked("creating");
        Ready(vm, "ready");
        Ready(vm, "reverting").Revert();
        Ready(vm, "deleting").RequestDelete();
        var deleted = Ready(vm, "deleted");
        deleted.RequestDelete();
        deleted.MarkDeleted();
        Ready(vm, "lost").MarkNotFound();
        vm.AddSnapshotUnchecked("failed").MarkRequestFailed("could not create", "raw");
        return vm;
    }

    // Q-C: billing follows what Incus holds, a revert or a delete in flight included.
    [Fact]
    public async Task SnapshotBilling_IsWhatIncusHolds_ADeleteInFlightIncluded()
    {
        using var db = NewDb();
        db.Vms.Add(VmWithEverySnapshotSituation());
        await db.SaveChangesAsync();

        var billed = (await new VmSnapshotRepository(db).ListBillableAsync()).Select(s => s.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "deleting", "ready", "reverting" }, billed);
    }

    [Fact] // a snapshot has no cluster; it lives where its VM lives. Only what has a provider id and is not gone.
    public async Task SnapshotReconcilable_FollowsItsVmsCluster_AndIsWhatReachedTheProvider()
    {
        using var db = NewDb();
        var here = VmWithEverySnapshotSituation("vn-hcm-1");
        var there = RunningVm("vn-han-1");
        Ready(there, "elsewhere");
        db.Vms.AddRange(here, there);
        await db.SaveChangesAsync();

        var names = (await new VmSnapshotRepository(db).ListReconcilableAsync("vn-hcm-1")).Select(s => s.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "deleting", "lost", "ready", "reverting" }, names);
    }

    [Fact]
    public async Task SnapshotList_ShowsEverythingButDeleted()
    {
        using var db = NewDb();
        var vm = VmWithEverySnapshotSituation();
        db.Vms.Add(vm);
        await db.SaveChangesAsync();

        var names = (await new VmSnapshotRepository(db).FindByVmIdAsync(vm.Id)).Select(s => s.Name).OrderBy(n => n).ToList();

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

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// A VM's, a network's and a rule's status and the Incus status seen while an operation waits, as Postgres stores them: what InMemory cannot show — filtered unique
/// indexes and the column spelling (titan-ops architecture/desired-and-current.md#desired-and-current).
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class StateRoundTripDbTests(PostgresFixture pg)
{
    // A rule is the network's child entity (titan-ops architecture/code-structure.md#child-entity): seeded through it.
    private static FirewallRule NewRule(Network network) =>
        network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");

    [Fact]
    public async Task WaitingStop_SurvivesAReload_WithItsIdKindAndTime()
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "rt-vm", "m", "tpl", "cluster-a", 20);
        vm.MarkRunning("42", "10.0.0.5");
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Vms.Add(vm);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            // The status and the Incus status are stored as their names.
            var stored = (await db.Database
                .SqlQueryRaw<string>("SELECT status || '|' || observed_state AS \"Value\" FROM core.vms WHERE id = {0}", vm.Id)
                .ToListAsync())[0];
            Assert.Equal("Stopping|Running", stored);

            var reloaded = await db.Vms.SingleAsync(v => v.Id == vm.Id);
            Assert.Equal(VmStatus.Stopping, reloaded.Status);
            Assert.Equal(VmStatus.Running, reloaded.InstanceStatus);
            Assert.Equal(stop, reloaded.Operation.PendingId!.Value);
            Assert.Equal(vm.Operation.AskedAt!.Value, reloaded.Operation.AskedAt!.Value, TimeSpan.FromMilliseconds(1));
        }
    }

    // The name stays taken while the delete is in flight, and is freed once the network is Deleted.
    [Fact]
    public async Task NetworkName_IsHeldDuringADelete_AndFreedOnceDeleted()
    {
        var tenantOwner = TestTenant.WithRoomToSpare();
        var tenant = tenantOwner.Id;
        var leaving = TestAggregates.Network(tenantOwner, "shared-name", "cluster-a");
        leaving.MarkActive("20", "10.0.0.0/24", "10.0.0.1");
        leaving.Delete();

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(leaving);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(TestAggregates.Network(tenantOwner, "shared-name", "cluster-a"));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var stored = await db.Networks.SingleAsync(n => n.Id == leaving.Id);
            stored.MarkDeleted();
            await db.SaveChangesAsync();

            db.Networks.Add(TestAggregates.Network(tenantOwner, "shared-name", "cluster-a"));
            await db.SaveChangesAsync(); // no violation: the filter drops the deleted row
        }
    }

    [Fact]
    public async Task FirewallRuleStatus_IsStoredAsItsName_DeletedRulesAreNotListed_AndAWaitingDeleteIsNotCounted()
    {
        Network network = TestAggregates.Network(TestTenant.WithRoomToSpare(), $"rt-net-{Guid.NewGuid():N}"[..12], "cluster-a");
        network.MarkActive("net-1", "10.0.0.0/24", "10.0.0.1");
        Guid networkId = network.Id;
        FirewallRule creating = NewRule(network);
        FirewallRule deleting = NewRule(network);
        deleting.MarkActive("rule-2");
        deleting.Delete();
        FirewallRule deleted = NewRule(network);
        deleted.MarkActive("rule-3");
        deleted.Delete();
        deleted.MarkDeleted();

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(network);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            List<string> stored = await db.Database
                .SqlQueryRaw<string>("SELECT status AS \"Value\" FROM core.firewall_rules WHERE id = {0}", deleting.Id)
                .ToListAsync();
            Assert.Equal("Deleting", stored.Single());
            // Incus spells the ACL rule state in lower case, and so does the column
            // (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
            List<string> state = await db.Database
                .SqlQueryRaw<string>("SELECT observed_state AS \"Value\" FROM core.firewall_rules WHERE id = {0}", deleting.Id)
                .ToListAsync();
            Assert.Equal("enabled", state.Single());

            FirewallRuleRepository repo = new(db);
            List<FirewallRule> listed = await repo.ListByNetworkAsync(networkId);
            // The cap is counted on the aggregate as the repository loads it back, rules included.
            Network reloaded = (await new NetworkRepository(db).FindByIdAsync(networkId))!;
            int counted = reloaded.FirewallRuleQuotaUsage;

            // The Deleted row is still in the table — it is the filter, not a missing row, that hides it.
            Assert.Equal(3, await db.FirewallRules.CountAsync(r => r.NetworkId == networkId));
            Assert.Equal(new[] { creating.Id, deleting.Id }.OrderBy(x => x), listed.Select(r => r.Id).OrderBy(x => x));
            Assert.Equal(1, counted); // the waiting delete stopped counting when it was asked for
        }
    }
}

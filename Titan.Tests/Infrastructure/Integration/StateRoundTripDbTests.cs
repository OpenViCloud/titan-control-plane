using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmSnapshot;
using Titan.Domain.Aggregates.FirewallRule;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// Desired/current state and the existence pair as Postgres stores them: what InMemory cannot show — filtered unique
/// indexes, the column spelling, and ExecuteUpdate (titan-ops architecture/desired-and-current.md#desired-and-current).
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class StateRoundTripDbTests(PostgresFixture pg)
{
    private static FirewallRule NewRule(Guid networkId)
    {
        return FirewallRule.CreateUnchecked(networkId, Guid.NewGuid(),
            FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");
    }

    private static string Shape(VmSnapshot s)
    {
        string transition = "-";
        if (s.Transition != null)
        {
            transition = s.Transition.Value.ToString();
        }
        return $"{s.CurrentExistence}/{s.DesiredExistence}/{transition}";
    }

    [Fact]
    public async Task WaitingStop_SurvivesAReload_AndResendsTheSameId()
    {
        var vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "rt-vm", "m", "tpl", "cluster-a", 20);
        vm.MarkRunning("42", "10.0.0.5");
        VmIntentTicket ticket = vm.RequestStop();

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Vms.Add(vm);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var stored = (await db.Database
                .SqlQueryRaw<string>("SELECT desired_state AS \"Value\" FROM core.vms WHERE id = {0}", vm.Id)
                .ToListAsync())[0];
            Assert.Equal("Stopped", stored);

            var reloaded = await db.Vms.SingleAsync(v => v.Id == vm.Id);
            Assert.Equal(VmIntent.Stop, reloaded.PendingIntent);
            Assert.Equal(ticket.IntentId, reloaded.ResendWaitingIntent(VmIntent.Stop).IntentId);
        }
    }

    // The name stays taken while the delete is in flight, and is freed once the network is Deleted.
    [Fact]
    public async Task NetworkName_IsHeldDuringADelete_AndFreedOnceDeleted()
    {
        var tenant = Guid.NewGuid();
        var leaving = Network.CreateUnchecked(tenant, "shared-name", "cluster-a");
        leaving.MarkActive("20", "10.0.0.0/24", "10.0.0.1");
        leaving.RequestDelete();

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(leaving);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Networks.Add(Network.CreateUnchecked(tenant, "shared-name", "cluster-a"));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var stored = await db.Networks.SingleAsync(n => n.Id == leaving.Id);
            stored.MarkDeleted();
            await db.SaveChangesAsync();

            db.Networks.Add(Network.CreateUnchecked(tenant, "shared-name", "cluster-a"));
            await db.SaveChangesAsync(); // no violation: the filter drops the deleted row
        }
    }

    [Fact]
    public async Task FirewallRuleState_IsStoredAsItsName_DeletedRulesAreNotListed_AndAWaitingDeleteIsNotCounted()
    {
        Guid networkId = Guid.NewGuid();
        FirewallRule creating = NewRule(networkId);
        FirewallRule deleting = NewRule(networkId);
        deleting.MarkActive("rule-2");
        deleting.RequestDelete();
        FirewallRule deleted = NewRule(networkId);
        deleted.MarkActive("rule-3");
        deleted.RequestDelete();
        deleted.MarkDeleted();

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.FirewallRules.AddRange(creating, deleting, deleted);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            List<string> stored = await db.Database
                .SqlQueryRaw<string>("SELECT desired_existence AS \"Value\" FROM core.firewall_rules WHERE id = {0}", deleting.Id)
                .ToListAsync();
            Assert.Equal("Deleted", stored.Single());
            // Incus spells the ACL rule state in lower case, and so does the column
            // (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
            List<string> state = await db.Database
                .SqlQueryRaw<string>("SELECT current_state AS \"Value\" FROM core.firewall_rules WHERE id = {0}", deleting.Id)
                .ToListAsync();
            Assert.Equal("enabled", state.Single());

            FirewallRuleRepository repo = new(db);
            List<FirewallRule> listed = await repo.ListByNetworkAsync(networkId);
            int counted = await repo.CountByNetworkAsync(networkId);

            // The Deleted row is still in the table — it is the filter, not a missing row, that hides it.
            Assert.Equal(3, await db.FirewallRules.CountAsync(r => r.NetworkId == networkId));
            Assert.Equal(new[] { creating.Id, deleting.Id }.OrderBy(x => x), listed.Select(r => r.Id).OrderBy(x => x));
            Assert.Equal(1, counted); // the waiting delete stopped counting when it was asked for
        }
    }

    // The VM's delete command confirmed it gone: every snapshot it had is gone with it, and whatever request was waiting on
    // them is over.
    [Fact]
    public async Task MarkAllDeletedByVmId_WritesDeletedAndEndsTheWait_OnPostgres()
    {
        var vmId = Guid.NewGuid();
        var otherVmId = Guid.NewGuid();
        var ready = VmSnapshot.CreateUnchecked(vmId, Guid.NewGuid(), "ready");
        ready.MarkCreated("1:1");
        var waitingDelete = VmSnapshot.CreateUnchecked(vmId, Guid.NewGuid(), "leaving");
        waitingDelete.MarkCreated("1:2");
        waitingDelete.RequestDelete();
        var otherVm = VmSnapshot.CreateUnchecked(otherVmId, Guid.NewGuid(), "other");
        otherVm.MarkCreated("2:1");

        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.VmSnapshots.AddRange(ready, waitingDelete, otherVm);
            await db.SaveChangesAsync();
            await new VmSnapshotRepository(db).MarkAllDeletedByVmIdAsync(vmId);
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var snapshots = await db.VmSnapshots.AsNoTracking()
                .Where(s => s.VmId == vmId || s.VmId == otherVmId)
                .ToDictionaryAsync(s => s.Name);
            Assert.Equal("Deleted/Exists/-", Shape(snapshots["ready"]));
            Assert.Equal("Deleted/Deleted/-", Shape(snapshots["leaving"]));
            Assert.Equal("Exists/Exists/-", Shape(snapshots["other"]));
        }
    }
}

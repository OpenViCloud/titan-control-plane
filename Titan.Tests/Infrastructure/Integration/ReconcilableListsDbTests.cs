using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// A row leaves a reconcile list only for what the list's filter names, never for a value in its other columns
/// (titan-ops features/network/user-stories.md#us-net-intent-04). The six list tests seed one row per cell of
/// external id × existence, written behind EF's back so a cell no aggregate method reaches is still there.
/// Category=Integration — its own CI step; needs real Postgres for the raw UPDATE and the query over those rows.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ReconcilableListsDbTests(PostgresFixture pg)
{
    private static readonly string[] Existences = ["NotCreated", "Exists", "NotFound", "Deleted"];

    private static string NewCluster() => $"cluster-{Guid.NewGuid():N}"[..20];

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    /// Puts each row in its own cell and returns the ids that have a provider id and are not Deleted.
    private async Task<List<Guid>> SpreadOverTheCellsAsync(string table, IReadOnlyList<Guid> rows)
    {
        await using TitanDbContext db = pg.NewDbContext();
        List<Guid> listed = [];
        int next = 0;
        foreach (string existence in Existences)
        {
            Guid withoutId = rows[next];
            string clearId = "UPDATE core." + table + " SET external_id = NULL, current_existence = {0} WHERE id = {1}";
            await db.Database.ExecuteSqlRawAsync(clearId, existence, withoutId);
            Guid withId = rows[next + 1];
            string setId = "UPDATE core." + table + " SET external_id = {0}, current_existence = {1} WHERE id = {2}";
            await db.Database.ExecuteSqlRawAsync(setId, Unique("ext"), existence, withId);
            next += 2;
            if (existence != "Deleted")
            {
                listed.Add(withId);
            }
        }
        return listed;
    }

    private static List<Guid> Sorted(IEnumerable<Guid> ids) => ids.OrderBy(id => id).ToList();

    [Fact]
    public async Task Networks_HoldOnlyRowsWithAnId_ThatAreNotDeleted()
    {
        string cluster = NewCluster();
        List<Network> rows = Enumerable.Range(0, 8)
            .Select(_ => Network.CreateUnchecked(Guid.NewGuid(), Unique("net"), cluster)).ToList();
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Networks.AddRange(rows);
            await seed.SaveChangesAsync();
        }
        List<Guid> expected = await SpreadOverTheCellsAsync("networks", rows.Select(n => n.Id).ToList());

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new NetworkRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted(expected), Sorted(listed.Select(n => n.Id)));
        Assert.All(listed, n => Assert.False(string.IsNullOrEmpty(n.ExternalId)));
    }

    [Fact]
    public async Task FirewallRules_HoldOnlyRowsWithAnId_ThatAreNotDeleted()
    {
        string cluster = NewCluster();
        Network network = Network.CreateUnchecked(Guid.NewGuid(), Unique("net"), cluster);
        List<FirewallRule> rows = Enumerable.Range(1, 8)
            .Select(port => network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, port, port, "0.0.0.0/0"))
            .ToList();
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Networks.Add(network);
            await seed.SaveChangesAsync();
        }
        List<Guid> expected = await SpreadOverTheCellsAsync("firewall_rules", rows.Select(r => r.Id).ToList());

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new FirewallRuleRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted(expected), Sorted(listed.Select(r => r.Id)));
        Assert.All(listed, r => Assert.Equal(network.Id, r.NetworkId));
        Assert.All(listed, r => Assert.False(string.IsNullOrEmpty(r.ExternalId)));
    }

    [Fact]
    public async Task Vms_HoldOnlyRowsWithAnId_ThatAreNotDeleted()
    {
        string cluster = NewCluster();
        string offeringId = await VmOfferingAsync();
        List<Vm> rows = Enumerable.Range(0, 8)
            .Select(_ => Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), Unique("vm"), offeringId, "tpl-1", cluster, 20)).ToList();
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Vms.AddRange(rows);
            await seed.SaveChangesAsync();
        }
        List<Guid> expected = await SpreadOverTheCellsAsync("vms", rows.Select(v => v.Id).ToList());

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new VmRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted(expected), Sorted(listed.Select(v => v.Id)));
        Assert.All(listed, v => Assert.False(string.IsNullOrEmpty(v.ExternalId)));
    }

    [Fact]
    public async Task VmSnapshots_HoldOnlyRowsWithAnId_ThatAreNotDeleted()
    {
        string cluster = NewCluster();
        string offeringId = await VmOfferingAsync();
        Vm vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), Unique("vm"), offeringId, "tpl-1", cluster, 20);
        List<VmSnapshot> rows = Enumerable.Range(0, 8).Select(i => vm.AddSnapshotUnchecked($"snap-{i}")).ToList();
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Vms.Add(vm);
            await seed.SaveChangesAsync();
        }
        List<Guid> expected = await SpreadOverTheCellsAsync("vm_snapshots", rows.Select(s => s.Id).ToList());

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new VmSnapshotRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted(expected), Sorted(listed.Select(s => s.Id)));
        Assert.All(listed, s => Assert.Equal(vm.Id, s.VmId));
        Assert.All(listed, s => Assert.False(string.IsNullOrEmpty(s.ExternalId)));
    }

    [Fact]
    public async Task Volumes_HoldOnlyRowsWithAnId_ThatAreNotDeleted()
    {
        string cluster = NewCluster();
        string offeringId = await VolumeOfferingAsync();
        List<Volume> rows = Enumerable.Range(0, 8)
            .Select(_ => Volume.CreateUnchecked(Guid.NewGuid(), Unique("vol"), cluster, offeringId, 20)).ToList();
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Volumes.AddRange(rows);
            await seed.SaveChangesAsync();
        }
        List<Guid> expected = await SpreadOverTheCellsAsync("volumes", rows.Select(v => v.Id).ToList());

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new VolumeRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted(expected), Sorted(listed.Select(v => v.Id)));
        Assert.All(listed, v => Assert.False(string.IsNullOrEmpty(v.ExternalId)));
    }

    [Fact]
    public async Task VolumeSnapshots_HoldOnlyRowsWithAnId_ThatAreNotDeleted()
    {
        string cluster = NewCluster();
        string offeringId = await VolumeOfferingAsync();
        Volume volume = Volume.CreateUnchecked(Guid.NewGuid(), Unique("vol"), cluster, offeringId, 20);
        List<VolumeSnapshot> rows = Enumerable.Range(0, 8).Select(i => volume.AddSnapshotUnchecked($"snap-{i}")).ToList();
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Volumes.Add(volume);
            await seed.SaveChangesAsync();
        }
        List<Guid> expected = await SpreadOverTheCellsAsync("volume_snapshots", rows.Select(s => s.Id).ToList());

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new VolumeSnapshotRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted(expected), Sorted(listed.Select(s => s.Id)));
        Assert.All(listed, s => Assert.Equal(volume.Id, s.VolumeId));
        Assert.All(listed, s => Assert.False(string.IsNullOrEmpty(s.ExternalId)));
    }

    // A value that breaks the Domain's rule fails where the row is loaded as an aggregate; the reconciler's list does
    // not load one, so the worker keeps the list, that row included.
    [Fact]
    public async Task ANetworkNameThatBreaksTheRule_DoesNotCostTheList()
    {
        string cluster = NewCluster();
        Network broken = Network.CreateUnchecked(Guid.NewGuid(), Unique("net"), cluster);
        broken.MarkCreated(Unique("ext"), "10.0.0.0/24", "10.0.0.1");
        Network healthy = Network.CreateUnchecked(Guid.NewGuid(), Unique("net"), cluster);
        healthy.MarkCreated(Unique("ext"), "10.0.1.0/24", "10.0.1.1");
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Networks.AddRange(broken, healthy);
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.networks SET name = {"-bad name"} WHERE id = {broken.Id}");
        }

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new NetworkRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted([broken.Id, healthy.Id]), Sorted(listed.Select(n => n.Id)));
    }

    [Fact]
    public async Task ARequestedCidrThatBreaksTheRule_DoesNotCostTheList()
    {
        string cluster = NewCluster();
        Network broken = Network.CreateUnchecked(Guid.NewGuid(), Unique("net"), cluster, requestedCidr: "10.0.0.0/24");
        broken.MarkCreated(Unique("ext"), "10.0.0.0/24", "10.0.0.1");
        Network healthy = Network.CreateUnchecked(Guid.NewGuid(), Unique("net"), cluster);
        healthy.MarkCreated(Unique("ext"), "10.0.1.0/24", "10.0.1.1");
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Networks.AddRange(broken, healthy);
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.networks SET requested_cidr = {"10.0.0.300/24"} WHERE id = {broken.Id}");
        }

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new NetworkRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted([broken.Id, healthy.Id]), Sorted(listed.Select(n => n.Id)));
    }

    [Fact]
    public async Task AFirewallRuleCidrThatBreaksTheRule_DoesNotCostTheList()
    {
        string cluster = NewCluster();
        Network network = Network.CreateUnchecked(Guid.NewGuid(), Unique("net"), cluster);
        FirewallRule broken = network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");
        broken.MarkCreated(Unique("ext"));
        FirewallRule healthy = network.AddFirewallRuleUnchecked(FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0");
        healthy.MarkCreated(Unique("ext"));
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Networks.Add(network);
            await seed.SaveChangesAsync();
            await seed.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.firewall_rules SET cidr = {"10.0.0.300/24"} WHERE id = {broken.Id}");
        }

        await using TitanDbContext db = pg.NewDbContext();
        var listed = await new FirewallRuleRepository(db).ListReconcilableAsync(cluster);

        Assert.Equal(Sorted([broken.Id, healthy.Id]), Sorted(listed.Select(r => r.Id)));
    }

    private async Task<string> VmOfferingAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await db.VmOfferings.FirstAsync()).Id;
    }

    private async Task<string> VolumeOfferingAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await db.VolumeOfferings.FirstAsync()).Id;
    }
}

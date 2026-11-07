using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with VM snapshots waiting on a request under the old column meets the migration that gives each request
/// its kind and time (titan-ops architecture/resource-lifecycle.md#shape): the kind is read in the order the snapshot
/// then read it (desired Deleted, then current NotCreated, else a revert) and, after the later migration that gives a
/// snapshot one status, is the status it waits in; an open command takes the request's key and id, and the view lists
/// the table. The old column name and keys below are the schema before it, by necessity. Category=Integration — its own
/// CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VmSnapshotPendingRequestMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261103081037_NetworkPendingRequest";

    private readonly TestPostgres _container = new();

    public Task InitializeAsync() => _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private async Task MigrateToAsync(string migration)
    {
        await using TitanDbContext db = NewDbContext();
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private async Task MigrateToLatestAsync()
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    // The VM the snapshots belong to, as the schema before this migration holds it (rollout phase 1 already applied).
    private async Task<Guid> SeedVmAsync()
    {
        Guid id = Guid.NewGuid();
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour)
VALUES ('m-mig', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING;
INSERT INTO core.vms (id, tenant_id, network_id, name, current_state, desired_state, current_existence, desired_existence,
                      external_id, vm_offering_id, template_id, cluster_id, disk_size_gb, created_at)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {"vm-" + id.ToString("N")[..8]}, 'Running', 'Running', 'Exists',
        'Exists', {"vm" + id.ToString("N")[..20]}, 'm-mig', 'tpl', 'cluster-a', 20, now())");
        return id;
    }

    // A snapshot row as the image before this migration wrote it: the request's id in pending_intent_id, its kind implied
    // by the two existences.
    private async Task<Guid> SeedSnapshotAsync(Guid vmId, string currentExistence, string desiredExistence, bool waiting)
    {
        Guid id = Guid.NewGuid();
        Guid? intent = waiting ? Guid.NewGuid() : null;
        string? externalId = currentExistence == "NotCreated" ? null : $"vm/s-{id.ToString("N")[..6]}";
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_snapshots (id, vm_id, tenant_id, name, current_existence, desired_existence, external_id, created_at,
                               pending_intent_id)
VALUES ({id}, {vmId}, {Guid.NewGuid()}, {"s-" + id.ToString("N")[..8]}, {currentExistence}, {desiredExistence},
        {externalId}, now(), {intent})");
        return id;
    }

    private async Task AddOpenCommandAsync(string key, string kind)
    {
        await using TitanDbContext seed = NewDbContext();
        await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, key, kind, "cluster-a", "{}");
    }

    private async Task<VmSnapshot> SnapshotAsync(Guid vmId, Guid snapshotId)
    {
        await using TitanDbContext db = NewDbContext();
        Vm vm = await db.Vms.Include(v => v.Snapshots).SingleAsync(v => v.Id == vmId);
        return vm.Snapshots.Single(s => s.Id == snapshotId);
    }

    [Fact]
    public async Task EveryWaitingRequest_GetsTheStatusOfItsKindInTheOrderTheOldModelReadIt_AndATime()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedVmAsync();
        Guid deleting = await SeedSnapshotAsync(vmId, "Exists", "Deleted", waiting: true);
        Guid deletingLost = await SeedSnapshotAsync(vmId, "NotFound", "Deleted", waiting: true);
        Guid creating = await SeedSnapshotAsync(vmId, "NotCreated", "Exists", waiting: true);
        Guid reverting = await SeedSnapshotAsync(vmId, "Exists", "Exists", waiting: true);
        Guid revertingLost = await SeedSnapshotAsync(vmId, "NotFound", "Exists", waiting: true);
        Guid resting = await SeedSnapshotAsync(vmId, "Exists", "Exists", waiting: false);

        await MigrateToLatestAsync();

        VmSnapshot delete = await SnapshotAsync(vmId, deleting);
        Assert.Equal(VmSnapshotStatus.Deleting, delete.Status);
        Assert.True(delete.Operation.AskedAt > DateTimeOffset.UnixEpoch);
        Assert.Equal(VmSnapshotStatus.Available, delete.InstanceStatus);
        VmSnapshot deleteLost = await SnapshotAsync(vmId, deletingLost);
        Assert.Equal(VmSnapshotStatus.Deleting, deleteLost.Status);
        Assert.Null(deleteLost.InstanceStatus);
        Assert.Equal(VmSnapshotStatus.Creating, (await SnapshotAsync(vmId, creating)).Status);
        VmSnapshot revert = await SnapshotAsync(vmId, reverting);
        Assert.Equal(VmSnapshotStatus.Reverting, revert.Status);
        Assert.Equal(VmSnapshotStatus.Available, revert.InstanceStatus);
        VmSnapshot revertLost = await SnapshotAsync(vmId, revertingLost);
        Assert.Equal(VmSnapshotStatus.Reverting, revertLost.Status);
        Assert.Null(revertLost.InstanceStatus);
        VmSnapshot rest = await SnapshotAsync(vmId, resting);
        Assert.Null(rest.Operation.PendingId);
        Assert.Equal(VmSnapshotStatus.Available, rest.Status);
    }

    [Fact]
    public async Task AnOpenCommand_TakesTheRequestsKeyAndId_AFinishedOneIsLeftAlone()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedVmAsync();
        Guid creating = await SeedSnapshotAsync(vmId, "NotCreated", "Exists", waiting: true);
        await AddOpenCommandAsync($"vm-snapshot-create-{creating}", ProviderCommandKinds.VmSnapshotCreate);
        Guid reverting = await SeedSnapshotAsync(vmId, "Exists", "Exists", waiting: true);
        await AddOpenCommandAsync($"vm-snapshot-revert-{reverting}", ProviderCommandKinds.VmSnapshotRevert);
        Guid deleting = await SeedSnapshotAsync(vmId, "Exists", "Deleted", waiting: true);
        await AddOpenCommandAsync($"vm-snapshot-delete-{deleting}", ProviderCommandKinds.VmSnapshotDelete);
        // An earlier revert of the snapshot now being deleted, done: its row is history, keyed as it was.
        await AddOpenCommandAsync($"vm-snapshot-revert-{deleting}", ProviderCommandKinds.VmSnapshotRevert);
        await using (TitanDbContext seed = NewDbContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($@"
UPDATE core.provider_commands SET status = 'Done' WHERE key = {"vm-snapshot-revert-" + deleting}");
        }

        await MigrateToLatestAsync();

        Guid createRequest = (await SnapshotAsync(vmId, creating)).Operation.PendingId!.Value;
        Guid revertRequest = (await SnapshotAsync(vmId, reverting)).Operation.PendingId!.Value;
        Guid deleteRequest = (await SnapshotAsync(vmId, deleting)).Operation.PendingId!.Value;
        await using TitanDbContext read = NewDbContext();
        Dictionary<string, string> payloadByKey = await read.ProviderCommands.ToDictionaryAsync(c => c.Key, c => c.Payload);
        Assert.Equal(4, payloadByKey.Count);
        Assert.Equal(createRequest.ToString(), RequestIdIn(payloadByKey[$"vm-snapshot-create-{creating}-{createRequest}"]));
        Assert.Equal(revertRequest.ToString(), RequestIdIn(payloadByKey[$"vm-snapshot-revert-{reverting}-{revertRequest}"]));
        Assert.Equal(deleteRequest.ToString(), RequestIdIn(payloadByKey[$"vm-snapshot-delete-{deleting}-{deleteRequest}"]));
        Assert.Equal("{}", payloadByKey[$"vm-snapshot-revert-{deleting}"]);
    }

    private static string? RequestIdIn(string payload)
    {
        using JsonDocument json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("requestId").GetString();
    }

    [Fact]
    public async Task TheView_ListsWaitingVmSnapshots_AndASnapshotOfTheNewModelInserts()
    {
        await MigrateToLatestAsync();
        Guid tenantId = Guid.NewGuid();
        Vm vm = Vm.Create(tenantId, Guid.NewGuid(), "vm-after", "m-mig", "tpl", "cluster-a", 20);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm-after", VmStatus.Running, "10.0.0.5", null);
        VmSnapshot reverting = vm.WithSnapshot("reverting");
        vm.SnapshotCreateSucceeded(reverting.Id, reverting.Operation.PendingId!.Value, "vm-after/s1");
        Guid revert = Guid.NewGuid();
        reverting.Revert(revert); // on the snapshot alone, so the view shows both kinds: a VM itself takes one create or revert at a time
        VmSnapshot creating = vm.WithSnapshot("creating");
        await using (TitanDbContext db = NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('m-mig', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
            db.Vms.Add(vm);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        List<string> rows = await read.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE tenant_id = {tenantId} AND resource = 'vm-snapshot' ORDER BY kind")
            .ToListAsync();
        Assert.Equal([$"vm-snapshot|Create|{creating.Operation.PendingId!.Value}", $"vm-snapshot|Revert|{revert}"], rows);
    }
}

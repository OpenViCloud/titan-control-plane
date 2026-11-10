using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with volumes and volume snapshots waiting on a request under the old column meets the migration that gives
/// each request its kind and time (titan-ops architecture/resource-lifecycle.md#shape): the kind is read in the order
/// the volume then read it and, after the later migration that gives a volume one status, is the status it waits in; a
/// detach a VM's delete asked for without a command ends, an open command takes the request's key and id, and the view
/// lists both tables. The old column name below is the schema before it, by necessity. Category=Integration — its own CI
/// step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VolumePendingRequestMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261102132441_VmPendingRequest";

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

    // A volume row as the image before this migration wrote it: the request's id in pending_intent_id, its kind implied by
    // the existence pair and the desired attachment.
    private async Task<Guid> SeedVolumeAsync(string currentExistence, string desiredExistence, Guid? desiredAttachedVmId,
        bool waiting, Guid? currentAttachedVmId = null)
    {
        Guid id = Guid.NewGuid();
        Guid? intent = waiting ? Guid.NewGuid() : null;
        string? externalId = currentExistence == "NotCreated" ? null : $"vol-{id:N}";
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.volume_offerings (id, name, size_gb, is_customized, price_per_gb_per_hour)
VALUES ('vol-mig', 'v', 10, false, 0.0001) ON CONFLICT (id) DO NOTHING;
INSERT INTO core.volumes (id, tenant_id, name, cluster_id, volume_offering_id, size_gb, current_existence, desired_existence,
                          current_attached_vm_id, current_attached_elsewhere, desired_attached_vm_id, external_id,
                          created_at, pending_intent_id)
VALUES ({id}, {Guid.NewGuid()}, {"vol-" + id.ToString("N")[..8]}, 'cluster-a', 'vol-mig', 10, {currentExistence},
        {desiredExistence}, {currentAttachedVmId}, false, {desiredAttachedVmId}, {externalId}, now(), {intent})");
        return id;
    }

    private async Task<Guid> SeedSnapshotAsync(Guid volumeId, string desiredExistence)
    {
        Guid id = Guid.NewGuid();
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.volume_snapshots (id, volume_id, tenant_id, name, current_existence, desired_existence, external_id,
                                   created_at, pending_intent_id)
VALUES ({id}, {volumeId}, {Guid.NewGuid()}, 's', 'Exists', {desiredExistence}, {"vol/s-" + id.ToString("N")[..6]}, now(),
        {Guid.NewGuid()})");
        return id;
    }

    private async Task AddOpenCommandAsync(string key, string kind)
    {
        await using TitanDbContext seed = NewDbContext();
        await ProviderCommandRowsBeforeOneRule.InsertAsync(seed, key, kind, "cluster-a", "{}");
    }

    private async Task<Volume> VolumeAsync(Guid id)
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Volumes.Include(v => v.Snapshots).SingleAsync(v => v.Id == id);
    }

    [Fact]
    public async Task EveryWaitingRequest_GetsTheStatusOfItsKindFromTheOldColumns_AndATime()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vm = Guid.NewGuid();
        Guid deleting = await SeedVolumeAsync("Exists", "Deleted", null, waiting: true);
        Guid creating = await SeedVolumeAsync("NotCreated", "Exists", null, waiting: true);
        Guid attaching = await SeedVolumeAsync("Exists", "Exists", vm, waiting: true);
        Guid detaching = await SeedVolumeAsync("Exists", "Exists", null, waiting: true, currentAttachedVmId: vm);
        await AddOpenCommandAsync($"volume-detach-{detaching}", ProviderCommandKinds.VolumeDetach);
        Guid resting = await SeedVolumeAsync("Exists", "Exists", null, waiting: false);
        Guid snapshotDeleting = await SeedSnapshotAsync(resting, "Deleted");
        Guid snapshotCreating = await SeedSnapshotAsync(resting, "Exists");

        await MigrateToLatestAsync();

        Assert.Equal(VolumeStatus.Deleting, (await VolumeAsync(deleting)).Status);
        Assert.Equal(VolumeStatus.Creating, (await VolumeAsync(creating)).Status);
        Assert.Equal(VolumeStatus.Attaching, (await VolumeAsync(attaching)).Status);
        Volume detach = await VolumeAsync(detaching);
        Assert.Equal(VolumeStatus.Detaching, detach.Status);
        Assert.True(detach.Operation.AskedAt > DateTimeOffset.UnixEpoch);
        Volume rest = await VolumeAsync(resting);
        Assert.Null(rest.Operation.PendingId);
        Assert.Equal(VolumeStatus.Available, rest.Status);
        Assert.Equal(VolumeSnapshotStatus.Deleting, rest.Snapshots.Single(s => s.Id == snapshotDeleting).Status);
        Assert.Equal(VolumeSnapshotStatus.Creating, rest.Snapshots.Single(s => s.Id == snapshotCreating).Status);
    }

    // A detach waiting with no open command — the one Volume.DetachWithDeletedVm left — has no report coming, so it ends;
    // the desired attachment it cleared stays clear, as a VM's delete now leaves a volume with nothing waiting.
    [Fact]
    public async Task ADetachWithoutAnOpenCommand_Ends_AndOneWithItKeepsWaiting()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vm = Guid.NewGuid();
        Guid askedByAVmDelete = await SeedVolumeAsync("Exists", "Exists", null, waiting: true, currentAttachedVmId: vm);
        Guid askedByTheCustomer = await SeedVolumeAsync("Exists", "Exists", null, waiting: true, currentAttachedVmId: vm);
        await AddOpenCommandAsync($"volume-detach-{askedByTheCustomer}", ProviderCommandKinds.VolumeDetach);

        await MigrateToLatestAsync();

        Volume ended = await VolumeAsync(askedByAVmDelete);
        Assert.Null(ended.Operation.PendingId);
        Assert.Equal(VolumeStatus.Available, ended.Status);
        Assert.Null(ended.DesiredAttachedVmId);
        Assert.Equal(vm, ended.CurrentAttachedVmId);
        Assert.Equal(VolumeStatus.Detaching, (await VolumeAsync(askedByTheCustomer)).Status);
    }

    [Fact]
    public async Task AnOpenCommand_TakesTheRequestsKeyAndId_ARestoreAsTheNewVolumesCreate()
    {
        await MigrateToAsync(MigrationBefore);
        Guid deleting = await SeedVolumeAsync("Exists", "Deleted", null, waiting: true);
        await AddOpenCommandAsync($"volume-delete-{deleting}", ProviderCommandKinds.VolumeDelete);
        Guid restoring = await SeedVolumeAsync("NotCreated", "Exists", null, waiting: true);
        await AddOpenCommandAsync($"volume-snapshot-restore-{restoring}", ProviderCommandKinds.VolumeSnapshotRestore);
        Guid resting = await SeedVolumeAsync("Exists", "Exists", null, waiting: false);
        Guid snapshot = await SeedSnapshotAsync(resting, "Deleted");
        await AddOpenCommandAsync($"volume-snapshot-delete-{snapshot}", ProviderCommandKinds.VolumeSnapshotDelete);

        await MigrateToLatestAsync();

        Guid deleteRequest = (await VolumeAsync(deleting)).Operation.PendingId!.Value;
        Guid restoreRequest = (await VolumeAsync(restoring)).Operation.PendingId!.Value;
        Guid snapshotRequest = (await VolumeAsync(resting)).Snapshots.Single().Operation.PendingId!.Value;
        await using TitanDbContext read = NewDbContext();
        Dictionary<string, string> payloadByKey = await read.ProviderCommands.ToDictionaryAsync(c => c.Key, c => c.Payload);
        Assert.Equal(deleteRequest.ToString(), RequestIdIn(payloadByKey[$"volume-delete-{deleting}-{deleteRequest}"]));
        Assert.Equal(restoreRequest.ToString(), RequestIdIn(payloadByKey[$"volume-create-{restoring}-{restoreRequest}"]));
        Assert.Equal(snapshotRequest.ToString(),
            RequestIdIn(payloadByKey[$"volume-snapshot-delete-{snapshot}-{snapshotRequest}"]));
    }

    private static string? RequestIdIn(string payload)
    {
        using JsonDocument json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("requestId").GetString();
    }

    [Fact]
    public async Task TheView_ListsWaitingVolumesAndSnapshots_AndAVolumeOfTheNewModelInserts()
    {
        await MigrateToLatestAsync();
        Volume volume = Volume.Create(Guid.NewGuid(), "after", "cluster-a", "vol-mig", 10);
        await using (TitanDbContext db = NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.volume_offerings (id, name, price_per_gb_per_hour) VALUES ('vol-mig', 'v', 0.0001) ON CONFLICT (id) DO NOTHING");
            volume.CreateSucceeded(volume.Operation.PendingId!.Value, "vol-after", false, null, false);
            volume.AddSnapshot(Guid.NewGuid(), "s");
            db.Volumes.Add(volume);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext read = NewDbContext();
        List<string> rows = await read.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind AS \"Value\" FROM core.pending_requests WHERE tenant_id = {volume.TenantId}")
            .ToListAsync();
        Assert.Equal(["volume-snapshot|Create"], rows);
    }
}

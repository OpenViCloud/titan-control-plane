using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with volumes and volume snapshots under the existence pair and the pending kind meets the migration that
/// gives each one status (titan-ops features/volume/design.md#volume-data-model): each combination of the old columns
/// becomes the status the migration's CASE names, in its order, and observed_state keeps Available only while an
/// operation waits on a resource Incus was seen holding. The view of waiting requests reads a volume's and a snapshot's
/// kind off its status, and Down puts the old columns back. The old column names below are the schema before it, by
/// necessity. Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VolumeSingleStatusMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261107161438_VmSnapshotSingleStatus";
    private const string ThisMigration = "20261108054110_VolumeSingleStatus";

    private const string VolumeTable = "volumes";
    private const string SnapshotTable = "volume_snapshots";

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

    private async Task SeedOfferingAsync()
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO core.volume_offerings (id, name, size_gb, is_customized, price_per_gb_per_hour) VALUES ('vol-mig', 'v', 10, false, 0.0001) ON CONFLICT (id) DO NOTHING");
    }

    // A volume row as the image before this migration wrote it. A waiting operation has its id and kind; a volume that
    // never reached Incus has no provider id. Desired is Deleted once a delete was asked, unless the row says otherwise.
    private async Task<Guid> SeedVolumeAsync(string currentExistence, string? pendingKind = null,
        bool namedOnIncus = true, string? desiredExistence = null, bool pendingId = true,
        Guid? currentAttachedVmId = null, Guid? desiredAttachedVmId = null)
    {
        await SeedOfferingAsync();
        Guid id = Guid.NewGuid();
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        if (pendingKind != null && pendingId)
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
        }
        string? externalId = null;
        if (namedOnIncus)
        {
            externalId = "vol" + id.ToString("N")[..20];
        }
        string desired = "Exists";
        if (currentExistence == "Deleted" || pendingKind == "Delete")
        {
            desired = "Deleted";
        }
        if (desiredExistence != null)
        {
            desired = desiredExistence;
        }
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.volumes (id, tenant_id, name, cluster_id, volume_offering_id, size_gb, current_existence, desired_existence,
                          current_attached_vm_id, current_attached_elsewhere, desired_attached_vm_id, external_id, created_at,
                          pending_request_id, pending_request_kind, pending_request_asked_at)
VALUES ({id}, {Guid.NewGuid()}, {"vol-" + id.ToString("N")[..8]}, 'cluster-a', 'vol-mig', 10, {currentExistence}, {desired},
        {currentAttachedVmId}, false, {desiredAttachedVmId}, {externalId}, now(),
        {requestId}, {pendingKind}, {askedAt})");
        return id;
    }

    // A snapshot row as the image before this migration wrote it, under a volume resting on Incus.
    private async Task<Guid> SeedSnapshotAsync(Guid volumeId, string currentExistence, string? pendingKind = null,
        bool namedOnIncus = true, string? desiredExistence = null, bool pendingId = true)
    {
        Guid id = Guid.NewGuid();
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        if (pendingKind != null && pendingId)
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
        }
        string? externalId = null;
        if (namedOnIncus)
        {
            externalId = "vol/s-" + id.ToString("N")[..12];
        }
        string desired = "Exists";
        if (currentExistence == "Deleted" || pendingKind == "Delete")
        {
            desired = "Deleted";
        }
        if (desiredExistence != null)
        {
            desired = desiredExistence;
        }
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.volume_snapshots (id, volume_id, tenant_id, name, current_existence, desired_existence, external_id, created_at,
                                   pending_request_id, pending_request_kind, pending_request_asked_at)
VALUES ({id}, {volumeId}, {Guid.NewGuid()}, {"s-" + id.ToString("N")[..8]}, {currentExistence}, {desired}, {externalId}, now(),
        {requestId}, {pendingKind}, {askedAt})");
        return id;
    }

    // Each row's status and observed_state after the migration, read in SQL: "-" stands for null. The table is one of
    // the two constants above; the statement is a literal for each, never built from the name.
    private async Task<Dictionary<Guid, string>> StatusAndObservedStateAsync(string table)
    {
        await using TitanDbContext db = NewDbContext();
        string sql = table == VolumeTable
            ? "SELECT id::text || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.volumes"
            : "SELECT id::text || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.volume_snapshots";
        List<string> rows = await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
        Dictionary<Guid, string> byId = new();
        foreach (string row in rows)
        {
            int cut = row.IndexOf('|');
            byId[Guid.Parse(row[..cut])] = row[(cut + 1)..];
        }
        return byId;
    }

    private async Task<Volume> VolumeAsync(Guid id)
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Volumes.Include(v => v.Snapshots).SingleAsync(v => v.Id == id);
    }

    [Fact]
    public async Task EveryOldVolumeCombination_BecomesTheStatusTheCaseNames_AndObservedStateIsAvailableOnlyWhileAnOperationWaitsOnAVolumeIncusHeld()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vm = Guid.NewGuid();
        // One row per branch of the CASE, in its order.
        Guid deleted = await SeedVolumeAsync("Deleted");
        // Deleted absorbs: a delete still marked waiting on a Deleted row is not waiting.
        Guid deletedWithADeleteWaiting = await SeedVolumeAsync("Deleted", pendingKind: "Delete");
        Guid deletingHeld = await SeedVolumeAsync("Exists", pendingKind: "Delete");
        Guid deletingLost = await SeedVolumeAsync("NotFound", pendingKind: "Delete");
        Guid attachingHeld = await SeedVolumeAsync("Exists", pendingKind: "Attach", desiredAttachedVmId: vm);
        Guid attachingLost = await SeedVolumeAsync("NotFound", pendingKind: "Attach", desiredAttachedVmId: vm);
        Guid detachingHeld = await SeedVolumeAsync("Exists", pendingKind: "Detach", currentAttachedVmId: vm);
        Guid detachingLost = await SeedVolumeAsync("NotFound", pendingKind: "Detach");
        Guid creating = await SeedVolumeAsync("NotCreated", pendingKind: "Create", namedOnIncus: false);
        // Seen on Incus while its create still waits: only the Domain reaches it (the reconciler lists rows with an id).
        Guid creatingSeen = await SeedVolumeAsync("Exists", pendingKind: "Create", namedOnIncus: false);
        Guid failed = await SeedVolumeAsync("NotCreated", namedOnIncus: false);
        // Without a provider id nothing is on Incus, whatever the existence said.
        Guid existsWithoutAnId = await SeedVolumeAsync("Exists", namedOnIncus: false);
        Guid notFound = await SeedVolumeAsync("NotFound");
        Guid available = await SeedVolumeAsync("Exists");
        // Combinations the CASE does not name end in its ELSE: Available.
        Guid notCreatedWithAnId = await SeedVolumeAsync("NotCreated");
        Guid desiredDeletedAtRest = await SeedVolumeAsync("Exists", desiredExistence: "Deleted");
        Guid aKindWithoutAnId = await SeedVolumeAsync("Exists", pendingKind: "Delete", pendingId: false);

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> byId = await StatusAndObservedStateAsync(VolumeTable);
        Assert.Equal("Deleted|-", byId[deleted]);
        Assert.Equal("Deleted|-", byId[deletedWithADeleteWaiting]);
        Assert.Equal("Deleting|Available", byId[deletingHeld]);
        Assert.Equal("Deleting|-", byId[deletingLost]);
        Assert.Equal("Attaching|Available", byId[attachingHeld]);
        Assert.Equal("Attaching|-", byId[attachingLost]);
        Assert.Equal("Detaching|Available", byId[detachingHeld]);
        Assert.Equal("Detaching|-", byId[detachingLost]);
        Assert.Equal("Creating|-", byId[creating]);
        Assert.Equal("Creating|Available", byId[creatingSeen]);
        Assert.Equal("Failed|-", byId[failed]);
        Assert.Equal("Failed|-", byId[existsWithoutAnId]);
        Assert.Equal("NotFound|-", byId[notFound]);
        Assert.Equal("Available|-", byId[available]);
        Assert.Equal("Available|-", byId[notCreatedWithAnId]);
        Assert.Equal("Available|-", byId[desiredDeletedAtRest]);
        Assert.Equal("Available|-", byId[aKindWithoutAnId]);
    }

    [Fact]
    public async Task EveryOldSnapshotCombination_BecomesTheStatusTheCaseNames_AndObservedStateIsAvailableOnlyWhileAnOperationWaitsOnASnapshotIncusHeld()
    {
        await MigrateToAsync(MigrationBefore);
        Guid volumeId = await SeedVolumeAsync("Exists");
        // One row per branch of the CASE, in its order.
        Guid deleted = await SeedSnapshotAsync(volumeId, "Deleted");
        // Deleted absorbs: a delete still marked waiting on a Deleted row is not waiting.
        Guid deletedWithADeleteWaiting = await SeedSnapshotAsync(volumeId, "Deleted", pendingKind: "Delete");
        Guid deletingHeld = await SeedSnapshotAsync(volumeId, "Exists", pendingKind: "Delete");
        Guid deletingLost = await SeedSnapshotAsync(volumeId, "NotFound", pendingKind: "Delete");
        Guid creating = await SeedSnapshotAsync(volumeId, "NotCreated", pendingKind: "Create", namedOnIncus: false);
        // Seen on Incus while its create still waits: only the Domain reaches it (the reconciler lists rows with an id).
        Guid creatingSeen = await SeedSnapshotAsync(volumeId, "Exists", pendingKind: "Create", namedOnIncus: false);
        Guid failed = await SeedSnapshotAsync(volumeId, "NotCreated", namedOnIncus: false);
        // Without a provider id nothing is on Incus, whatever the existence said.
        Guid existsWithoutAnId = await SeedSnapshotAsync(volumeId, "Exists", namedOnIncus: false);
        Guid notFound = await SeedSnapshotAsync(volumeId, "NotFound");
        Guid available = await SeedSnapshotAsync(volumeId, "Exists");
        // Combinations the CASE does not name end in its ELSE: Available.
        Guid notCreatedWithAnId = await SeedSnapshotAsync(volumeId, "NotCreated");
        Guid desiredDeletedAtRest = await SeedSnapshotAsync(volumeId, "Exists", desiredExistence: "Deleted");
        Guid aKindWithoutAnId = await SeedSnapshotAsync(volumeId, "Exists", pendingKind: "Delete", pendingId: false);

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> byId = await StatusAndObservedStateAsync(SnapshotTable);
        Assert.Equal("Deleted|-", byId[deleted]);
        Assert.Equal("Deleted|-", byId[deletedWithADeleteWaiting]);
        Assert.Equal("Deleting|Available", byId[deletingHeld]);
        Assert.Equal("Deleting|-", byId[deletingLost]);
        Assert.Equal("Creating|-", byId[creating]);
        Assert.Equal("Creating|Available", byId[creatingSeen]);
        Assert.Equal("Failed|-", byId[failed]);
        Assert.Equal("Failed|-", byId[existsWithoutAnId]);
        Assert.Equal("NotFound|-", byId[notFound]);
        Assert.Equal("Available|-", byId[available]);
        Assert.Equal("Available|-", byId[notCreatedWithAnId]);
        Assert.Equal("Available|-", byId[desiredDeletedAtRest]);
        Assert.Equal("Available|-", byId[aKindWithoutAnId]);
    }

    // What the domain reads off the migrated rows: the status, whether Incus holds the resource while an operation
    // waits, and the attachment columns the migration leaves alone.
    [Fact]
    public async Task AMigratedVolume_ReadsBackItsStatus_AndWhetherIncusHoldsIt_AndItsAttachment()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vm = Guid.NewGuid();
        Guid attachingHeld = await SeedVolumeAsync("Exists", pendingKind: "Attach", desiredAttachedVmId: vm);
        Guid detachingHeld = await SeedVolumeAsync("Exists", pendingKind: "Detach", currentAttachedVmId: vm);
        Guid detachingLost = await SeedVolumeAsync("NotFound", pendingKind: "Detach");
        Guid creating = await SeedVolumeAsync("NotCreated", pendingKind: "Create", namedOnIncus: false);
        Guid failed = await SeedVolumeAsync("NotCreated", namedOnIncus: false);
        Guid deletingHeld = await SeedVolumeAsync("Exists", pendingKind: "Delete");
        Guid resting = await SeedVolumeAsync("Exists");
        Guid snapshotDeletingHeld = await SeedSnapshotAsync(resting, "Exists", pendingKind: "Delete");
        Guid snapshotDeletingLost = await SeedSnapshotAsync(resting, "NotFound", pendingKind: "Delete");

        await MigrateToAsync(ThisMigration);

        Volume attach = await VolumeAsync(attachingHeld);
        Assert.Equal(VolumeStatus.Attaching, attach.Status);
        Assert.Equal(VolumeStatus.Available, attach.InstanceStatus);
        Assert.True(attach.IsBilled);
        Assert.True(attach.OccupiesQuota);
        Assert.NotNull(attach.Operation.PendingId);
        Assert.Equal(vm, attach.DesiredAttachedVmId);
        Volume detachHeld = await VolumeAsync(detachingHeld);
        Assert.Equal(VolumeStatus.Detaching, detachHeld.Status);
        Assert.Equal(VolumeStatus.Available, detachHeld.InstanceStatus);
        Assert.Equal(vm, detachHeld.CurrentAttachedVmId);
        Volume detachLost = await VolumeAsync(detachingLost);
        Assert.Equal(VolumeStatus.Detaching, detachLost.Status);
        Assert.Null(detachLost.InstanceStatus);
        Assert.False(detachLost.IsBilled);
        Assert.False(detachLost.OccupiesQuota);
        Volume create = await VolumeAsync(creating);
        Assert.Equal(VolumeStatus.Creating, create.Status);
        Assert.Null(create.InstanceStatus);
        Assert.False(create.IsBilled);
        Assert.True(create.OccupiesQuota);
        Volume fail = await VolumeAsync(failed);
        Assert.Equal(VolumeStatus.Failed, fail.Status);
        Assert.Null(fail.Operation.PendingId);
        Assert.False(fail.IsBilled);
        Assert.False(fail.OccupiesQuota);
        Volume delete = await VolumeAsync(deletingHeld);
        Assert.Equal(VolumeStatus.Deleting, delete.Status);
        Assert.True(delete.IsBilled);
        Assert.False(delete.OccupiesQuota);
        Volume rest = await VolumeAsync(resting);
        Assert.Equal(VolumeStatus.Available, rest.Status);
        VolumeSnapshot snapshotHeld = rest.Snapshots.Single(s => s.Id == snapshotDeletingHeld);
        Assert.Equal(VolumeSnapshotStatus.Deleting, snapshotHeld.Status);
        Assert.Equal(VolumeSnapshotStatus.Available, snapshotHeld.InstanceStatus);
        Assert.True(snapshotHeld.IsBilled);
        Assert.False(snapshotHeld.CountsTowardQuota);
        VolumeSnapshot snapshotLost = rest.Snapshots.Single(s => s.Id == snapshotDeletingLost);
        Assert.Equal(VolumeSnapshotStatus.Deleting, snapshotLost.Status);
        Assert.Null(snapshotLost.InstanceStatus);
        Assert.False(snapshotLost.IsBilled);
    }

    [Theory]
    [InlineData(VolumeTable)]
    [InlineData(SnapshotTable)]
    public async Task TheOldColumnsAreGone_AndTheStatusAndObservedStateAreThere(string table)
    {
        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> columns = await db.Database
            .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = {table}")
            .ToListAsync();
        Assert.DoesNotContain("current_existence", columns);
        Assert.DoesNotContain("desired_existence", columns);
        Assert.DoesNotContain("pending_request_kind", columns);
        Assert.Contains("status", columns);
        Assert.Contains("observed_state", columns);
        // What the migration leaves alone.
        Assert.Contains("pending_request_id", columns);
        Assert.Contains("pending_request_asked_at", columns);
        Assert.Contains("request_failure_reason", columns);
    }

    [Fact]
    public async Task TheAttachmentColumnsOfAVolumeStay()
    {
        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> columns = await db.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'volumes'")
            .ToListAsync();
        Assert.Contains("current_attached_vm_id", columns);
        Assert.Contains("current_attached_elsewhere", columns);
        Assert.Contains("desired_attached_vm_id", columns);
    }

    // The view of waiting requests names a volume's kind from its status, the four words PendingRequestReader joins on, and
    // a snapshot's from the two it has.
    [Fact]
    public async Task TheViewOfWaitingRequests_NamesAVolumesAndASnapshotsKind_FromItsStatus()
    {
        await MigrateToAsync(MigrationBefore);
        Guid creating = await SeedVolumeAsync("NotCreated", pendingKind: "Create", namedOnIncus: false);
        Guid attaching = await SeedVolumeAsync("Exists", pendingKind: "Attach", desiredAttachedVmId: Guid.NewGuid());
        Guid detaching = await SeedVolumeAsync("Exists", pendingKind: "Detach", currentAttachedVmId: Guid.NewGuid());
        Guid deleting = await SeedVolumeAsync("NotFound", pendingKind: "Delete");
        Guid resting = await SeedVolumeAsync("Exists");
        Guid snapshotCreating = await SeedSnapshotAsync(resting, "NotCreated", pendingKind: "Create", namedOnIncus: false);
        Guid snapshotDeleting = await SeedSnapshotAsync(resting, "Exists", pendingKind: "Delete");
        Guid snapshotResting = await SeedSnapshotAsync(resting, "Exists");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> volumes = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'volume'")
            .ToListAsync();
        Assert.Equal(
            new[] { $"{creating}|Create", $"{attaching}|Attach", $"{detaching}|Detach", $"{deleting}|Delete" }
                .Order(StringComparer.Ordinal),
            volumes.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(volumes, r => r.StartsWith(resting.ToString(), StringComparison.Ordinal));
        List<string> snapshots = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'volume-snapshot'")
            .ToListAsync();
        Assert.Equal(
            new[] { $"{snapshotCreating}|Create", $"{snapshotDeleting}|Delete" }.Order(StringComparer.Ordinal),
            snapshots.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(snapshots, r => r.StartsWith(snapshotResting.ToString(), StringComparison.Ordinal));
    }

    // A volume and snapshots written by the model of today store their status and what Incus was seen holding by name.
    [Fact]
    public async Task VolumesAndSnapshotsWrittenNow_StoreTheirStatusAndObservedStateByName()
    {
        await MigrateToAsync(ThisMigration);
        await SeedOfferingAsync();
        Guid tenantId = Guid.NewGuid();
        Volume creating = Volume.Create(tenantId, "creating", "cluster-a", "vol-mig", 10);
        Volume holder = Volume.Create(tenantId, "holder", "cluster-a", "vol-mig", 10);
        holder.MarkCreated($"vol-{holder.Id:N}");
        VolumeSnapshot deleting = holder.WithSnapshot("deleting");
        holder.MarkSnapshotCreated(deleting.Id, $"vol-{holder.Id:N}/s1");
        holder.DeleteSnapshot(deleting.Id);
        VolumeSnapshot snapshotCreating = holder.WithSnapshot("creating");
        Volume deletingVolume = Volume.Create(tenantId, "going", "cluster-a", "vol-mig", 10);
        deletingVolume.MarkCreated($"vol-{deletingVolume.Id:N}");
        deletingVolume.Delete();
        await using (TitanDbContext db = NewDbContext())
        {
            db.Volumes.AddRange(creating, holder, deletingVolume);
            await db.SaveChangesAsync();
        }

        Dictionary<Guid, string> volumes = await StatusAndObservedStateAsync(VolumeTable);
        Assert.Equal("Creating|-", volumes[creating.Id]);
        Assert.Equal("Available|-", volumes[holder.Id]);
        Assert.Equal("Deleting|Available", volumes[deletingVolume.Id]);
        Dictionary<Guid, string> snapshots = await StatusAndObservedStateAsync(SnapshotTable);
        Assert.Equal("Deleting|Available", snapshots[deleting.Id]);
        Assert.Equal("Creating|-", snapshots[snapshotCreating.Id]);
        await using TitanDbContext read = NewDbContext();
        List<string> waiting = await read.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE tenant_id = {tenantId}")
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                $"volume|Create|{creating.Operation.PendingId!.Value}",
                $"volume|Delete|{deletingVolume.Operation.PendingId!.Value}",
                $"volume-snapshot|Delete|{deleting.Operation.PendingId!.Value}",
                $"volume-snapshot|Create|{snapshotCreating.Operation.PendingId!.Value}",
            }.Order(StringComparer.Ordinal),
            waiting.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Down_PutsTheOldColumnsBackFromTheStatus_AndTheViewReadsTheKindColumnAgain()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vm = Guid.NewGuid();
        Guid deletingHeld = await SeedVolumeAsync("Exists", pendingKind: "Delete");
        Guid deletingLost = await SeedVolumeAsync("NotFound", pendingKind: "Delete");
        Guid attachingHeld = await SeedVolumeAsync("Exists", pendingKind: "Attach", desiredAttachedVmId: vm);
        Guid attachingLost = await SeedVolumeAsync("NotFound", pendingKind: "Attach", desiredAttachedVmId: vm);
        Guid detachingHeld = await SeedVolumeAsync("Exists", pendingKind: "Detach", currentAttachedVmId: vm);
        Guid detachingLost = await SeedVolumeAsync("NotFound", pendingKind: "Detach");
        Guid creating = await SeedVolumeAsync("NotCreated", pendingKind: "Create", namedOnIncus: false);
        Guid creatingSeen = await SeedVolumeAsync("Exists", pendingKind: "Create", namedOnIncus: false);
        Guid failed = await SeedVolumeAsync("NotCreated", namedOnIncus: false);
        Guid notFound = await SeedVolumeAsync("NotFound");
        Guid available = await SeedVolumeAsync("Exists");
        Guid deleted = await SeedVolumeAsync("Deleted");
        Guid snapshotDeletingHeld = await SeedSnapshotAsync(available, "Exists", pendingKind: "Delete");
        Guid snapshotDeletingLost = await SeedSnapshotAsync(available, "NotFound", pendingKind: "Delete");
        Guid snapshotCreating = await SeedSnapshotAsync(available, "NotCreated", pendingKind: "Create", namedOnIncus: false);
        Guid snapshotCreatingSeen = await SeedSnapshotAsync(available, "Exists", pendingKind: "Create", namedOnIncus: false);
        Guid snapshotFailed = await SeedSnapshotAsync(available, "NotCreated", namedOnIncus: false);
        Guid snapshotNotFound = await SeedSnapshotAsync(available, "NotFound");
        Guid snapshotAvailable = await SeedSnapshotAsync(available, "Exists");
        Guid snapshotDeleted = await SeedSnapshotAsync(available, "Deleted");
        await MigrateToAsync(ThisMigration);

        await MigrateToAsync(MigrationBefore);

        await using TitanDbContext db = NewDbContext();
        List<string> volumes = await db.Database.SqlQueryRaw<string>(
            "SELECT id::text || '|' || current_existence || '|' || desired_existence || '|' || coalesce(pending_request_kind, '-') AS \"Value\" FROM core.volumes")
            .ToListAsync();
        Assert.Contains($"{deletingHeld}|Exists|Deleted|Delete", volumes);
        Assert.Contains($"{deletingLost}|NotFound|Deleted|Delete", volumes);
        Assert.Contains($"{attachingHeld}|Exists|Exists|Attach", volumes);
        Assert.Contains($"{attachingLost}|NotFound|Exists|Attach", volumes);
        Assert.Contains($"{detachingHeld}|Exists|Exists|Detach", volumes);
        Assert.Contains($"{detachingLost}|NotFound|Exists|Detach", volumes);
        Assert.Contains($"{creating}|NotCreated|Exists|Create", volumes);
        Assert.Contains($"{creatingSeen}|Exists|Exists|Create", volumes);
        Assert.Contains($"{failed}|NotCreated|Exists|-", volumes);
        Assert.Contains($"{notFound}|NotFound|Exists|-", volumes);
        Assert.Contains($"{available}|Exists|Exists|-", volumes);
        Assert.Contains($"{deleted}|Deleted|Deleted|-", volumes);
        List<string> snapshots = await db.Database.SqlQueryRaw<string>(
            "SELECT id::text || '|' || current_existence || '|' || desired_existence || '|' || coalesce(pending_request_kind, '-') AS \"Value\" FROM core.volume_snapshots")
            .ToListAsync();
        Assert.Contains($"{snapshotDeletingHeld}|Exists|Deleted|Delete", snapshots);
        Assert.Contains($"{snapshotDeletingLost}|NotFound|Deleted|Delete", snapshots);
        Assert.Contains($"{snapshotCreating}|NotCreated|Exists|Create", snapshots);
        Assert.Contains($"{snapshotCreatingSeen}|Exists|Exists|Create", snapshots);
        Assert.Contains($"{snapshotFailed}|NotCreated|Exists|-", snapshots);
        Assert.Contains($"{snapshotNotFound}|NotFound|Exists|-", snapshots);
        Assert.Contains($"{snapshotAvailable}|Exists|Exists|-", snapshots);
        Assert.Contains($"{snapshotDeleted}|Deleted|Deleted|-", snapshots);
        List<string> volumesWaiting = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'volume'")
            .ToListAsync();
        Assert.Contains($"{deletingHeld}|Delete", volumesWaiting);
        Assert.Contains($"{attachingLost}|Attach", volumesWaiting);
        Assert.Contains($"{detachingHeld}|Detach", volumesWaiting);
        Assert.Contains($"{creating}|Create", volumesWaiting);
        List<string> snapshotsWaiting = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'volume-snapshot'")
            .ToListAsync();
        Assert.Contains($"{snapshotDeletingHeld}|Delete", snapshotsWaiting);
        Assert.Contains($"{snapshotCreating}|Create", snapshotsWaiting);
    }
}

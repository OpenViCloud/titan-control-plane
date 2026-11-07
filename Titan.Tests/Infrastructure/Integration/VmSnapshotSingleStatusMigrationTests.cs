using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with VM snapshots under the existence pair and the pending kind meets the migration that gives a snapshot
/// one status (titan-ops features/vm/design.md#vm-data-model): each combination of the old columns becomes the status the
/// migration's CASE names, in its order, and observed_state keeps Available only while an operation waits on a snapshot
/// Incus was seen holding. The view of waiting requests reads a snapshot's kind off its status, and Down puts the old
/// columns back. The old column names below are the schema before it, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VmSnapshotSingleStatusMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261107123233_NetworkSingleStatus";
    private const string ThisMigration = "20261107161438_VmSnapshotSingleStatus";

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

    private static Vm RunningVm()
    {
        Vm vm = Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "vm-" + Guid.NewGuid().ToString("N")[..8], "m-mig", "tpl", "cluster-a", 20);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm" + vm.Id.ToString("N")[..20], VmStatus.Running, "10.0.0.5", null);
        return vm;
    }

    // Saves a VM, with whatever snapshots it holds, under the offering it names.
    private async Task SaveAsync(Vm vm)
    {
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('m-mig', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
    }

    // The VM the snapshots belong to, without snapshots. This migration does not touch core.vms, so the model of today
    // writes it at the migration before as well.
    private async Task<Guid> SeedVmAsync()
    {
        Vm vm = RunningVm();
        await SaveAsync(vm);
        return vm.Id;
    }

    // A snapshot row as the image before this migration wrote it. A waiting operation has its id and kind; a snapshot
    // that never reached Incus has no provider id. Desired is Deleted once a delete was asked, unless the row says
    // otherwise.
    private async Task<Guid> SeedSnapshotAsync(Guid vmId, string currentExistence, string? pendingKind = null,
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
            externalId = "vm/s-" + id.ToString("N")[..12];
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
INSERT INTO core.vm_snapshots (id, vm_id, tenant_id, name, current_existence, desired_existence, external_id, created_at,
                               pending_request_id, pending_request_kind, pending_request_asked_at)
VALUES ({id}, {vmId}, {Guid.NewGuid()}, {"s-" + id.ToString("N")[..8]}, {currentExistence}, {desired}, {externalId}, now(),
        {requestId}, {pendingKind}, {askedAt})");
        return id;
    }

    // Each row's status and observed_state after the migration, read in SQL: "-" stands for null.
    private async Task<Dictionary<Guid, string>> StatusAndObservedStateAsync()
    {
        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.vm_snapshots")
            .ToListAsync();
        Dictionary<Guid, string> byId = new();
        foreach (string row in rows)
        {
            int cut = row.IndexOf('|');
            byId[Guid.Parse(row[..cut])] = row[(cut + 1)..];
        }
        return byId;
    }

    [Fact]
    public async Task EveryOldCombination_BecomesTheStatusTheCaseNames_AndObservedStateIsAvailableOnlyWhileAnOperationWaitsOnASnapshotIncusHeld()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedVmAsync();
        // One row per branch of the CASE, in its order.
        Guid deleted = await SeedSnapshotAsync(vmId, "Deleted");
        // Deleted absorbs: a delete still marked waiting on a Deleted row is not waiting.
        Guid deletedWithADeleteWaiting = await SeedSnapshotAsync(vmId, "Deleted", pendingKind: "Delete");
        Guid deletingHeld = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Delete");
        Guid deletingLost = await SeedSnapshotAsync(vmId, "NotFound", pendingKind: "Delete");
        Guid revertingHeld = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Revert");
        Guid revertingLost = await SeedSnapshotAsync(vmId, "NotFound", pendingKind: "Revert");
        Guid creating = await SeedSnapshotAsync(vmId, "NotCreated", pendingKind: "Create", namedOnIncus: false);
        // Seen on Incus while its create still waits: only the Domain reaches it (the reconciler lists rows with an id).
        Guid creatingSeen = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Create", namedOnIncus: false);
        Guid failed = await SeedSnapshotAsync(vmId, "NotCreated", namedOnIncus: false);
        // Without a provider id nothing is on Incus, whatever the existence said.
        Guid existsWithoutAnId = await SeedSnapshotAsync(vmId, "Exists", namedOnIncus: false);
        Guid notFound = await SeedSnapshotAsync(vmId, "NotFound");
        Guid available = await SeedSnapshotAsync(vmId, "Exists");
        // Combinations the CASE does not name end in its ELSE: Available.
        Guid notCreatedWithAnId = await SeedSnapshotAsync(vmId, "NotCreated");
        Guid desiredDeletedAtRest = await SeedSnapshotAsync(vmId, "Exists", desiredExistence: "Deleted");
        Guid aKindWithoutAnId = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Delete", pendingId: false);

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> byId = await StatusAndObservedStateAsync();
        Assert.Equal("Deleted|-", byId[deleted]);
        Assert.Equal("Deleted|-", byId[deletedWithADeleteWaiting]);
        Assert.Equal("Deleting|Available", byId[deletingHeld]);
        Assert.Equal("Deleting|-", byId[deletingLost]);
        Assert.Equal("Reverting|Available", byId[revertingHeld]);
        Assert.Equal("Reverting|-", byId[revertingLost]);
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

    // What the domain reads off the migrated row: the status, and whether Incus holds the snapshot while it waits.
    [Fact]
    public async Task AMigratedSnapshot_ReadsBackItsStatus_AndWhetherIncusHoldsIt()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedVmAsync();
        Guid revertingHeld = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Revert");
        Guid deletingLost = await SeedSnapshotAsync(vmId, "NotFound", pendingKind: "Delete");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        Vm vm = await db.Vms.Include(v => v.Snapshots).SingleAsync(v => v.Id == vmId);
        VmSnapshot revert = vm.Snapshots.Single(s => s.Id == revertingHeld);
        Assert.Equal(VmSnapshotStatus.Reverting, revert.Status);
        Assert.Equal(VmSnapshotStatus.Available, revert.InstanceStatus);
        Assert.True(revert.IsBilled);
        VmSnapshot delete = vm.Snapshots.Single(s => s.Id == deletingLost);
        Assert.Equal(VmSnapshotStatus.Deleting, delete.Status);
        Assert.Null(delete.InstanceStatus);
        Assert.False(delete.IsBilled);
    }

    [Fact]
    public async Task TheOldColumnsAreGone_AndTheStatusAndObservedStateAreThere()
    {
        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> columns = await db.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'core' AND table_name = 'vm_snapshots'")
            .ToListAsync();
        Assert.DoesNotContain("current_existence", columns);
        Assert.DoesNotContain("desired_existence", columns);
        Assert.DoesNotContain("pending_request_kind", columns);
        Assert.Contains("status", columns);
        Assert.Contains("observed_state", columns);
    }

    // The view of waiting requests names a snapshot's kind from its status, the three words PendingRequestReader joins on.
    [Fact]
    public async Task TheViewOfWaitingRequests_NamesASnapshotsKind_FromItsStatus()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedVmAsync();
        Guid creating = await SeedSnapshotAsync(vmId, "NotCreated", pendingKind: "Create", namedOnIncus: false);
        Guid reverting = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Revert");
        Guid deleting = await SeedSnapshotAsync(vmId, "NotFound", pendingKind: "Delete");
        Guid resting = await SeedSnapshotAsync(vmId, "Exists");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'vm-snapshot'")
            .ToListAsync();
        Assert.Equal(
            new[] { $"{creating}|Create", $"{reverting}|Revert", $"{deleting}|Delete" }.Order(StringComparer.Ordinal),
            rows.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(rows, r => r.StartsWith(resting.ToString(), StringComparison.Ordinal));
    }

    // A snapshot written by the model of today stores its status and what Incus was seen holding by name.
    [Fact]
    public async Task ASnapshotWrittenNow_StoresItsStatusAndObservedStateByName()
    {
        await MigrateToAsync(ThisMigration);
        Vm vm = RunningVm();
        VmSnapshot snapshot = vm.WithSnapshot("nightly");
        snapshot.MarkCreated($"{vm.ExternalId}/s1");
        Guid revert = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, revert);
        await SaveAsync(vm);

        Dictionary<Guid, string> byId = await StatusAndObservedStateAsync();
        Assert.Equal("Reverting|Available", byId[snapshot.Id]);
        await using TitanDbContext db = NewDbContext();
        List<string> waiting = await db.Database
            .SqlQueryRaw<string>("SELECT kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE resource = 'vm-snapshot'")
            .ToListAsync();
        Assert.Equal([$"Revert|{revert}"], waiting);
    }

    [Fact]
    public async Task Down_PutsTheOldColumnsBackFromTheStatus_AndTheViewReadsTheKindColumnAgain()
    {
        await MigrateToAsync(MigrationBefore);
        Guid vmId = await SeedVmAsync();
        Guid deletingHeld = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Delete");
        Guid deletingLost = await SeedSnapshotAsync(vmId, "NotFound", pendingKind: "Delete");
        Guid revertingHeld = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Revert");
        Guid revertingLost = await SeedSnapshotAsync(vmId, "NotFound", pendingKind: "Revert");
        Guid creating = await SeedSnapshotAsync(vmId, "NotCreated", pendingKind: "Create", namedOnIncus: false);
        Guid creatingSeen = await SeedSnapshotAsync(vmId, "Exists", pendingKind: "Create", namedOnIncus: false);
        Guid failed = await SeedSnapshotAsync(vmId, "NotCreated", namedOnIncus: false);
        Guid notFound = await SeedSnapshotAsync(vmId, "NotFound");
        Guid available = await SeedSnapshotAsync(vmId, "Exists");
        Guid deleted = await SeedSnapshotAsync(vmId, "Deleted");
        await MigrateToAsync(ThisMigration);

        await MigrateToAsync(MigrationBefore);

        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database.SqlQueryRaw<string>(
            "SELECT id::text || '|' || current_existence || '|' || desired_existence || '|' || coalesce(pending_request_kind, '-') AS \"Value\" FROM core.vm_snapshots")
            .ToListAsync();
        Assert.Contains($"{deletingHeld}|Exists|Deleted|Delete", rows);
        Assert.Contains($"{deletingLost}|NotFound|Deleted|Delete", rows);
        Assert.Contains($"{revertingHeld}|Exists|Exists|Revert", rows);
        Assert.Contains($"{revertingLost}|NotFound|Exists|Revert", rows);
        Assert.Contains($"{creating}|NotCreated|Exists|Create", rows);
        Assert.Contains($"{creatingSeen}|Exists|Exists|Create", rows);
        Assert.Contains($"{failed}|NotCreated|Exists|-", rows);
        Assert.Contains($"{notFound}|NotFound|Exists|-", rows);
        Assert.Contains($"{available}|Exists|Exists|-", rows);
        Assert.Contains($"{deleted}|Deleted|Deleted|-", rows);
        List<string> waiting = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || kind AS \"Value\" FROM core.pending_requests WHERE resource = 'vm-snapshot'")
            .ToListAsync();
        Assert.Contains($"{deletingHeld}|Delete", waiting);
        Assert.Contains($"{revertingLost}|Revert", waiting);
        Assert.Contains($"{creating}|Create", waiting);
    }
}

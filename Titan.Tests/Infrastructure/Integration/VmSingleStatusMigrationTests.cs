using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// A database with VMs under the desired and existence pairs and the pending kind meets the migration that gives the VM
/// one status (titan-ops features/vm/design.md#vm-status): each combination of the old columns becomes the status the
/// migration's CASE names, in its order, a combination it does not name becomes Unknown, and observed_state keeps the old
/// current_state word for word. The old column names below are the schema before it, by necessity.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
public sealed class VmSingleStatusMigrationTests : IAsyncLifetime
{
    private const string MigrationBefore = "20261105044857_VmDropsItsPreRequestColumns";
    private const string ThisMigration = "20261105105217_VmSingleStatus";

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

    // A VM row as the image before this migration wrote it. A waiting request has its id and kind; a VM that never
    // reached Incus has no provider id.
    private async Task<(Guid Id, Guid? RequestId)> SeedVmAsync(string currentExistence, string? currentState,
        string? pendingKind = null, string? failureReason = null)
    {
        Guid id = Guid.NewGuid();
        Guid? requestId = null;
        DateTimeOffset? askedAt = null;
        if (pendingKind != null)
        {
            requestId = Guid.NewGuid();
            askedAt = DateTimeOffset.UtcNow;
        }
        string? externalId = null;
        if (currentExistence != "NotCreated")
        {
            externalId = "vm" + id.ToString("N")[..20];
        }
        string desiredExistence = "Exists";
        if (currentExistence == "Deleted" || pendingKind == "Delete")
        {
            desiredExistence = "Deleted";
        }
        string desiredState = "Running";
        if (pendingKind == "Stop" || currentState == "Stopped")
        {
            desiredState = "Stopped";
        }
        await using TitanDbContext db = NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour)
VALUES ('m-single', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING;
INSERT INTO core.vms (id, tenant_id, network_id, name, current_state, desired_state, current_existence, desired_existence,
                      external_id, vm_offering_id, template_id, cluster_id, disk_size_gb, created_at,
                      pending_request_id, pending_request_kind, pending_request_asked_at, request_failure_reason)
VALUES ({id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {"vm-" + id.ToString("N")[..8]}, {currentState}, {desiredState},
        {currentExistence}, {desiredExistence}, {externalId}, 'm-single', 'tpl', 'cluster-a', 20, now(),
        {requestId}, {pendingKind}, {askedAt}, {failureReason})");
        return (id, requestId);
    }

    // Each VM's status and observed_state after the migration, read in SQL: "-" stands for a null word.
    private async Task<Dictionary<Guid, string>> StatusAndWordByVmAsync()
    {
        await using TitanDbContext db = NewDbContext();
        List<string> rows = await db.Database
            .SqlQueryRaw<string>("SELECT id::text || '|' || status || '|' || coalesce(observed_state, '-') AS \"Value\" FROM core.vms")
            .ToListAsync();
        Dictionary<Guid, string> byVm = new();
        foreach (string row in rows)
        {
            int cut = row.IndexOf('|');
            byVm[Guid.Parse(row[..cut])] = row[(cut + 1)..];
        }
        return byVm;
    }

    [Fact]
    public async Task EveryOldCombination_BecomesTheStatusTheCaseNames_AndObservedStateKeepsTheOldWord()
    {
        await MigrateToAsync(MigrationBefore);
        // One row per branch of the CASE, in its order.
        var deleted = await SeedVmAsync("Deleted", null);
        var creating = await SeedVmAsync("NotCreated", null, pendingKind: "Create");
        var starting = await SeedVmAsync("Exists", "Stopped", pendingKind: "Start");
        var stopping = await SeedVmAsync("Exists", "Running", pendingKind: "Stop");
        var deleting = await SeedVmAsync("Exists", "Running", pendingKind: "Delete");
        var notFound = await SeedVmAsync("NotFound", null);
        var failed = await SeedVmAsync("NotCreated", null, failureReason: "The VM could not be created.");
        var running = await SeedVmAsync("Exists", "Running");
        var ready = await SeedVmAsync("Exists", "Ready");
        var stopped = await SeedVmAsync("Exists", "Stopped");
        var frozen = await SeedVmAsync("Exists", "Frozen");
        var error = await SeedVmAsync("Exists", "Error");
        // The order of the CASE: a waiting request names the move before what the reconciler last saw.
        var stopWaitingNotFound = await SeedVmAsync("NotFound", null, pendingKind: "Stop");
        // Combinations the CASE does not name: the reconciler writes the real status on its next cycle.
        var existsWithoutAWord = await SeedVmAsync("Exists", null);
        var notCreatedWithoutAReason = await SeedVmAsync("NotCreated", null);

        await MigrateToAsync(ThisMigration);

        Dictionary<Guid, string> byVm = await StatusAndWordByVmAsync();
        Assert.Equal("Deleted|-", byVm[deleted.Id]);
        Assert.Equal("Creating|-", byVm[creating.Id]);
        Assert.Equal("Starting|Stopped", byVm[starting.Id]);
        Assert.Equal("Stopping|Running", byVm[stopping.Id]);
        Assert.Equal("Deleting|Running", byVm[deleting.Id]);
        Assert.Equal("NotFound|-", byVm[notFound.Id]);
        Assert.Equal("Failed|-", byVm[failed.Id]);
        Assert.Equal("Running|Running", byVm[running.Id]);
        Assert.Equal("Ready|Ready", byVm[ready.Id]);
        Assert.Equal("Stopped|Stopped", byVm[stopped.Id]);
        Assert.Equal("Frozen|Frozen", byVm[frozen.Id]);
        Assert.Equal("Error|Error", byVm[error.Id]);
        Assert.Equal("Stopping|-", byVm[stopWaitingNotFound.Id]);
        Assert.Equal("Unknown|-", byVm[existsWithoutAWord.Id]);
        Assert.Equal("Unknown|-", byVm[notCreatedWithoutAReason.Id]);
        Assert.Equal(15, byVm.Count);
    }

    // The model reads the migrated rows: the waiting request keeps its id, and the status and the word load as enums.
    [Fact]
    public async Task TheModel_ReadsAMigratedVm_WithItsWaitingRequest()
    {
        await MigrateToAsync(MigrationBefore);
        var stopping = await SeedVmAsync("Exists", "Running", pendingKind: "Stop");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext read = NewDbContext();
        Vm vm = await read.Vms.SingleAsync(v => v.Id == stopping.Id);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(stopping.RequestId, vm.PendingOperationId);
        Assert.NotNull(vm.PendingOperationAskedAt);
    }

    // The view of waiting requests reads a VM's kind off its status once the kind column is gone.
    [Fact]
    public async Task TheView_ReadsEachWaitingVmsKind_OffItsStatus()
    {
        await MigrateToAsync(MigrationBefore);
        var creating = await SeedVmAsync("NotCreated", null, pendingKind: "Create");
        var starting = await SeedVmAsync("Exists", "Stopped", pendingKind: "Start");
        var stopping = await SeedVmAsync("Exists", "Running", pendingKind: "Stop");
        var deleting = await SeedVmAsync("Exists", "Running", pendingKind: "Delete");
        await SeedVmAsync("Exists", "Running");

        await MigrateToAsync(ThisMigration);

        await using TitanDbContext read = NewDbContext();
        List<string> rows = await read.Database
            .SqlQueryRaw<string>("SELECT kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE resource = 'vm' ORDER BY kind")
            .ToListAsync();
        Assert.Equal([
            $"Create|{creating.RequestId}",
            $"Delete|{deleting.RequestId}",
            $"Start|{starting.RequestId}",
            $"Stop|{stopping.RequestId}",
        ], rows);
    }
}

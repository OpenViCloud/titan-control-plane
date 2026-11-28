using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The claim query is the SQL of ProviderCommand.IsClaimable, HoldsTheLaneOf and HoldsTheResourceOf
// (titan-ops architecture/domain-methods.md#plain-code): every shape a row or a lane can be in goes through both, so
// the two cannot drift apart.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it); the races of two
// workers on one row or on two rows of one lane are ProviderCommandLeaseDbTests.
public class ProviderCommandRepositoryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"provider-command-state-{Guid.NewGuid()}")
            .Options);

    // Each shape in a tenant of its own: the lane of a tenant is measured apart, below.
    private static ProviderCommand Pending(string key, DateTimeOffset createdAt, Guid? tenantId = null,
        string kind = ProviderCommandKinds.VmDelete) =>
        ProviderCommand.Create(key, kind, "mock", "{}", createdAt, tenantId ?? Guid.NewGuid());

    private static ProviderCommand Leased(string key, DateTimeOffset leasedAt)
    {
        ProviderCommand row = Pending(key, leasedAt);
        row.Lease(Guid.NewGuid(), leasedAt);
        return row;
    }

    // Every shape, named by its key; the key is what the assertions list.
    private static List<ProviderCommand> EveryShape()
    {
        ProviderCommand done = Leased("done", Now.AddHours(-1));
        done.Complete(done.LeaseId!.Value);
        ProviderCommand failed = Leased("failed", Now.AddHours(-1));
        failed.Fail(failed.LeaseId!.Value, "gave up", retryable: false, Now.AddHours(-1));
        ProviderCommand retryWaiting = Leased("retry-waiting", Now);
        retryWaiting.Fail(retryWaiting.LeaseId!.Value, "try again", retryable: true, Now);

        return
        [
            Pending("pending-due", Now.AddMinutes(-1)),
            Pending("pending-now", Now),
            Pending("pending-later", Now.AddMinutes(5)),
            Leased("lease-ran-out", Now.AddHours(-1)),
            Leased("lease-runs-out-now", Now - ProviderCommand.LeaseLength),
            Leased("lease-held", Now),
            done, failed, retryWaiting,
        ];
    }

    [Fact]
    public async Task Claimable_IsAPendingRowThatIsDue_OrALeaseThatRanOut()
    {
        await using TitanDbContext db = NewDb();
        db.ProviderCommands.AddRange(EveryShape());
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync([ProviderCommandKinds.VmDelete], Now, 100, default);

        Assert.Equal(["lease-ran-out", "lease-runs-out-now", "pending-due", "pending-now"],
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ClaimQuery_AgreesWithIsClaimable_ForEveryShape()
    {
        await using TitanDbContext db = NewDb();
        List<ProviderCommand> shapes = EveryShape();
        db.ProviderCommands.AddRange(shapes);
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync([ProviderCommandKinds.VmDelete], Now, 100, default);

        Assert.Equal(shapes.Where(c => c.IsClaimable(Now)).Select(c => c.Key).Order(StringComparer.Ordinal),
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    private static readonly Guid Busy = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Idle = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // Lanes: a busy tenant (a held lease, a pending row, a pending backup: its backup lane is free), a busy no-tenant
    // lane, a tenant whose backup lane is held but its command lane is free, a tenant whose only leased row ran out
    // (that row is the one handed out), and two tenants whose earlier command is Done or Failed (neither holds the lane).
    private static List<ProviderCommand> EveryLane()
    {
        ProviderCommand ranOut = Leased("ran-out-tenant-leased", Now.AddHours(-1));
        Guid ranOutTenant = ranOut.TenantId!.Value;
        ProviderCommand syncHeld = ProviderCommand.Create("sync-held", ProviderCommandKinds.TemplateSync, "mock", "{}", Now);
        syncHeld.Lease(Guid.NewGuid(), Now);
        ProviderCommand busyHeld = Pending("busy-held", Now.AddMinutes(-2), Busy);
        busyHeld.Lease(Guid.NewGuid(), Now);
        ProviderCommand idleBackupHeld = Pending("idle-backup-held", Now.AddMinutes(-2), Idle, ProviderCommandKinds.VmBackup);
        idleBackupHeld.Lease(Guid.NewGuid(), Now);
        ProviderCommand done = Leased("done-tenant-done", Now.AddMinutes(-3));
        done.Complete(done.LeaseId!.Value);
        ProviderCommand failed = Leased("failed-tenant-failed", Now.AddMinutes(-3));
        failed.Fail(failed.LeaseId!.Value, "gave up", retryable: false, Now.AddMinutes(-3));
        return
        [
            busyHeld,
            Pending("busy-waiting", Now.AddMinutes(-1), Busy),
            Pending("busy-backup", Now.AddMinutes(-1), Busy, ProviderCommandKinds.VmBackup),
            syncHeld,
            ProviderCommand.Create("sync-waiting", ProviderCommandKinds.TemplateSync, "mock", "{}", Now.AddMinutes(-1)),
            idleBackupHeld,
            Pending("idle-waiting", Now.AddMinutes(-1), Idle),
            ranOut,
            Pending("ran-out-tenant-waiting", Now.AddMinutes(-1), ranOutTenant),
            done,
            Pending("done-tenant-waiting", Now.AddMinutes(-1), done.TenantId),
            failed,
            Pending("failed-tenant-waiting", Now.AddMinutes(-1), failed.TenantId),
        ];
    }

    private static readonly string[] CommandKinds =
        [ProviderCommandKinds.VmDelete, ProviderCommandKinds.TemplateSync, ProviderCommandKinds.VmBackup];

    [Fact]
    public async Task Claimable_SkipsALaneThatHoldsALease_ButNotTheLeaseThatRanOut()
    {
        await using TitanDbContext db = NewDb();
        db.ProviderCommands.AddRange(EveryLane());
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync(CommandKinds, Now, 100, default);

        Assert.Equal(["busy-backup", "done-tenant-waiting", "failed-tenant-waiting", "idle-waiting", "ran-out-tenant-leased"],
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    // A restore drill is a row of no tenant and no resource that the worker leases on a loop apart from the command and
    // backup loops (with the admin's restores of a backup archive); while it runs
    // (hours, on a big disk) the backup loop and a tenant's command still get their rows
    // (titan-ops features/backup/design.md#restore-drill).
    [Fact]
    public async Task ALeasedRestoreDrill_HoldsBackNoBackup_NorTheClustersConfig_NorATenantsCommand()
    {
        await using TitanDbContext db = NewDb();
        ProviderCommand drill = ProviderCommand.Create("drill", ProviderCommandKinds.RestoreDrill, "mock", "{}", Now.AddHours(-1));
        drill.Lease(Guid.NewGuid(), Now);
        Guid tenant = Guid.NewGuid();
        db.ProviderCommands.AddRange(
            drill,
            ProviderCommand.Create("vm-backup", ProviderCommandKinds.VmBackup, "mock", "{}", Now.AddMinutes(-3), tenant, Guid.NewGuid()),
            ProviderCommand.Create("config", ProviderCommandKinds.ClusterBackup, "mock", "{}", Now.AddMinutes(-2)),
            ProviderCommand.Create("vm-stop", ProviderCommandKinds.VmStop, "mock", "{}", Now.AddMinutes(-1), tenant, Guid.NewGuid()));
        await db.SaveChangesAsync();
        var repository = new ProviderCommandRepository(db);

        var backups = await repository.ListClaimableAsync(
            [ProviderCommandKinds.VmBackup, ProviderCommandKinds.VolumeBackup, ProviderCommandKinds.ClusterBackup], Now, 100, default);
        var commands = await repository.ListClaimableAsync([ProviderCommandKinds.VmStop], Now, 100, default);

        Assert.Equal(["config", "vm-backup"], backups.Select(c => c.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["vm-stop"], commands.Select(c => c.Key));
    }

    // What the worker's third loop asks for: the drill, and an admin's restores of a backup archive
    // (titan-ops features/backup/design.md#restore-as-new-resource).
    private static readonly string[] DrillAndRestoreKinds =
        [ProviderCommandKinds.RestoreDrill, ProviderCommandKinds.VmRestore, ProviderCommandKinds.VolumeRestore];

    // A restore is a row of no tenant that names the new VM or volume, leased with the drill. While it runs (an import of
    // a whole disk), a tenant's command, a template sync and a backup still get their rows; the drill and the other
    // restores wait for it.
    [Theory]
    [InlineData(ProviderCommandKinds.VmRestore)]
    [InlineData(ProviderCommandKinds.VolumeRestore)]
    public async Task ALeasedRestore_HoldsBackNoBackup_NorATenantsCommand_AndTheDrillAndTheOtherRestoresWait(string restoreKind)
    {
        await using TitanDbContext db = NewDb();
        ProviderCommand restore = ProviderCommand.Create("restore", restoreKind, "mock", "{}", Now.AddHours(-1), null, Guid.NewGuid());
        restore.Lease(Guid.NewGuid(), Now);
        Guid tenant = Guid.NewGuid();
        db.ProviderCommands.AddRange(
            restore,
            ProviderCommand.Create("vm-backup", ProviderCommandKinds.VmBackup, "mock", "{}", Now.AddMinutes(-7), tenant, Guid.NewGuid()),
            ProviderCommand.Create("config", ProviderCommandKinds.ClusterBackup, "mock", "{}", Now.AddMinutes(-6)),
            ProviderCommand.Create("vm-stop", ProviderCommandKinds.VmStop, "mock", "{}", Now.AddMinutes(-5), tenant, Guid.NewGuid()),
            ProviderCommand.Create("template-sync", ProviderCommandKinds.TemplateSync, "mock", "{}", Now.AddMinutes(-4)),
            ProviderCommand.Create("drill", ProviderCommandKinds.RestoreDrill, "mock", "{}", Now.AddMinutes(-3)),
            ProviderCommand.Create("vm-restore", ProviderCommandKinds.VmRestore, "mock", "{}", Now.AddMinutes(-2), null, Guid.NewGuid()),
            ProviderCommand.Create("volume-restore", ProviderCommandKinds.VolumeRestore, "mock", "{}", Now.AddMinutes(-1), null, Guid.NewGuid()));
        await db.SaveChangesAsync();
        var repository = new ProviderCommandRepository(db);

        var backups = await repository.ListClaimableAsync(
            [ProviderCommandKinds.VmBackup, ProviderCommandKinds.VolumeBackup, ProviderCommandKinds.ClusterBackup], Now, 100, default);
        var commands = await repository.ListClaimableAsync([ProviderCommandKinds.VmStop, ProviderCommandKinds.TemplateSync], Now, 100, default);
        var drillAndRestores = await repository.ListClaimableAsync(DrillAndRestoreKinds, Now, 100, default);

        Assert.Equal(["config", "vm-backup"], backups.Select(c => c.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["template-sync", "vm-stop"], commands.Select(c => c.Key).Order(StringComparer.Ordinal));
        Assert.Empty(drillAndRestores);
    }

    // The reverse: a drill that runs holds the restores back, and once it ended they are handed out oldest first.
    [Fact]
    public async Task ALeasedRestoreDrill_HoldsTheRestoresBack_UntilItEnds()
    {
        await using TitanDbContext db = NewDb();
        ProviderCommand drill = ProviderCommand.Create("drill", ProviderCommandKinds.RestoreDrill, "mock", "{}", Now.AddHours(-1));
        Guid lease = Guid.NewGuid();
        drill.Lease(lease, Now);
        db.ProviderCommands.AddRange(
            drill,
            ProviderCommand.Create("volume-restore", ProviderCommandKinds.VolumeRestore, "mock", "{}", Now.AddMinutes(-2), null, Guid.NewGuid()),
            ProviderCommand.Create("vm-restore", ProviderCommandKinds.VmRestore, "mock", "{}", Now.AddMinutes(-1), null, Guid.NewGuid()));
        await db.SaveChangesAsync();
        var repository = new ProviderCommandRepository(db);

        Assert.Empty(await repository.ListClaimableAsync(DrillAndRestoreKinds, Now, 100, default));

        drill.Complete(lease);
        await db.SaveChangesAsync();
        Assert.Equal(["volume-restore", "vm-restore"],
            (await repository.ListClaimableAsync(DrillAndRestoreKinds, Now, 100, default)).Select(c => c.Key));
    }

    [Fact]
    public async Task ClaimQuery_AgreesWithHoldsTheLaneOf_ForEveryLane()
    {
        await using TitanDbContext db = NewDb();
        List<ProviderCommand> lanes = EveryLane();
        db.ProviderCommands.AddRange(lanes);
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync(CommandKinds, Now, 100, default);

        IEnumerable<string> expected = lanes
            .Where(c => CommandKinds.Contains(c.Kind) && c.IsClaimable(Now))
            .Where(c => !lanes.Any(other => CommandKinds.Contains(other.Kind) && other.HoldsTheLaneOf(c)))
            .Select(c => c.Key);
        Assert.Equal(expected.Order(StringComparer.Ordinal), claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    // Rows of one VM in two lanes and of no resource: the resource rule reads every kind, not only the lease's.
    private static List<ProviderCommand> EveryResource()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        Guid vmX = Guid.NewGuid();
        Guid vmY = Guid.NewGuid();
        ProviderCommand backupX = ProviderCommand.Create("backup-x", ProviderCommandKinds.VmBackup, "mock", "{}", Now.AddMinutes(-5), tenantA, vmX);
        backupX.Lease(Guid.NewGuid(), Now.AddSeconds(-10));
        ProviderCommand ranOutY = ProviderCommand.Create("backup-y", ProviderCommandKinds.VmBackup, "mock", "{}", Now.AddMinutes(-5), tenantB, vmY);
        ranOutY.Lease(Guid.NewGuid(), Now.AddMinutes(-2));
        ProviderCommand doneX = ProviderCommand.Create("done-x", ProviderCommandKinds.VmStart, "mock", "{}", Now.AddMinutes(-6), Guid.NewGuid(), vmX);
        doneX.Lease(Guid.NewGuid(), Now.AddMinutes(-6));
        doneX.Complete(doneX.LeaseId!.Value);
        return
        [
            backupX, ranOutY, doneX,
            ProviderCommand.Create("waits-x", ProviderCommandKinds.VmDelete, "mock", "{}", Now.AddMinutes(-4), Guid.NewGuid(), vmX),
            ProviderCommand.Create("waits-y", ProviderCommandKinds.VmDelete, "mock", "{}", Now.AddMinutes(-4), Guid.NewGuid(), vmY),
            ProviderCommand.Create("free-z", ProviderCommandKinds.VmDelete, "mock", "{}", Now.AddMinutes(-4), Guid.NewGuid(), Guid.NewGuid()),
            ProviderCommand.Create("no-resource", ProviderCommandKinds.VmDelete, "mock", "{}", Now.AddMinutes(-4), Guid.NewGuid()),
        ];
    }

    [Fact]
    public async Task ClaimQuery_AgreesWithHoldsTheResourceOf_AcrossLanes()
    {
        await using TitanDbContext db = NewDb();
        List<ProviderCommand> rows = EveryResource();
        db.ProviderCommands.AddRange(rows);
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync(CommandKinds, Now, 100, default);

        IEnumerable<string> expected = rows
            .Where(c => CommandKinds.Contains(c.Kind) && c.IsClaimable(Now))
            .Where(c => !rows.Any(other => CommandKinds.Contains(other.Kind) && other.HoldsTheLaneOf(c)))
            .Where(c => !rows.Any(other => other.HoldsTheResourceOf(c)))
            .Select(c => c.Key);
        Assert.Equal(["backup-y", "free-z", "no-resource"], expected.Order(StringComparer.Ordinal));
        Assert.Equal(expected.Order(StringComparer.Ordinal), claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }
}

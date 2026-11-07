using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What a VM's operation does to its row, on Postgres (titan-ops features/vm/design.md#vm-status): each move writes the
/// six columns the VmOperation keeps, a row written before the status seen was kept only while an operation waits reads
/// with the same answers, and the next write of such a row leaves nothing of the old shape. InMemory cannot show the
/// column spelling, the replacement of the owned object on a tracked VM, or the SQL of the billed query.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VmOperationDbTests(PostgresFixture pg)
{
    private static Vm NewVm(Guid tenantId, string name) =>
        Vm.Create(tenantId, Guid.NewGuid(), name, "m", "tpl", "cluster-a", 20);

    // status | an operation is pending | its time is set | observed_state | reason | detail | a failure time is set
    private static async Task<string> RowOf(TitanDbContext db, Guid id) =>
        (await db.Database.SqlQueryRaw<string>(
            "SELECT status || '|' || (pending_request_id IS NOT NULL) || '|' || (pending_request_asked_at IS NOT NULL) || '|' || "
            + "coalesce(observed_state, '-') || '|' || coalesce(request_failure_reason, '-') || '|' || "
            + "coalesce(request_failure_detail, '-') || '|' || (request_failed_at IS NOT NULL) AS \"Value\" "
            + "FROM core.vms WHERE id = {0}", id).ToListAsync())[0];

    private async Task<string> RowOf(Guid id)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await RowOf(db, id);
    }

    [Fact]
    public async Task EveryMove_WritesItsColumns_AlsoWhenTheVmIsTrackedAcrossThem()
    {
        Vm vm = NewVm(TestTenant.WithRoomToSpare().Id, "op-db-1");
        await using TitanDbContext db = pg.NewDbContext();
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        Assert.Equal("Creating|true|true|-|-|-|false", await RowOf(vm.Id));

        vm.CreateSucceeded(vm.Operation.PendingId!.Value, "vm42", VmStatus.Running, "10.0.0.5", null);
        await db.SaveChangesAsync();
        Assert.Equal("Running|false|false|-|-|-|false", await RowOf(vm.Id));

        vm.Stop(Guid.NewGuid());
        await db.SaveChangesAsync();
        Assert.Equal("Stopping|true|true|Running|-|-|false", await RowOf(vm.Id));

        vm.MarkObserved(VmStatus.Stopped, null, null);
        await db.SaveChangesAsync();
        Assert.Equal("Stopping|true|true|Stopped|-|-|false", await RowOf(vm.Id));

        vm.StopFailed(vm.Operation.PendingId!.Value, "NO_ROOM", "raw detail");
        await db.SaveChangesAsync();
        Assert.Equal("Stopped|false|false|-|NO_ROOM|raw detail|true", await RowOf(vm.Id));

        vm.Start(Guid.NewGuid());
        await db.SaveChangesAsync();
        Assert.Equal("Starting|true|true|Stopped|NO_ROOM|raw detail|true", await RowOf(vm.Id));

        vm.StartSucceeded(vm.Operation.PendingId!.Value, VmStatus.Running, "10.0.0.5", null);
        await db.SaveChangesAsync();
        Assert.Equal("Running|false|false|-|-|-|false", await RowOf(vm.Id));

        vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s");
        await db.SaveChangesAsync();
        Assert.Equal("Snapshotting|false|false|Running|-|-|false", await RowOf(vm.Id));
    }

    [Fact]
    public async Task AReloadedVm_ReadsBackWhatEachMoveWrote()
    {
        Vm vm = NewVm(TestTenant.WithRoomToSpare().Id, "op-db-2");
        vm.MarkRunning("vm42", "10.0.0.5");
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);
        vm.MarkObserved(VmStatus.Stopped, null, null);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Vms.Add(vm);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext reading = pg.NewDbContext();
        Vm reloaded = await reading.Vms.SingleAsync(v => v.Id == vm.Id);

        Assert.Equal(VmStatus.Stopping, reloaded.Status);
        Assert.Equal(stop, reloaded.Operation.PendingId);
        Assert.Equal(vm.Operation.AskedAt!.Value, reloaded.Operation.AskedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(VmStatus.Stopped, reloaded.Operation.LastSeenStatus);
        Assert.Equal(VmStatus.Stopped, reloaded.InstanceStatus);
        Assert.Null(reloaded.Operation.FailureReason);
    }

    [Fact]
    public async Task AVmThatNeverHadAnOperation_IsNotNull_WhenItsColumnsAreAllNull()
    {
        Vm vm = NewVm(TestTenant.WithRoomToSpare().Id, "op-db-3");
        vm.MarkRunning("vm42", "10.0.0.5");
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Vms.Add(vm);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext reading = pg.NewDbContext();
        Vm reloaded = await reading.Vms.SingleAsync(v => v.Id == vm.Id);

        Assert.Equal("Running|false|false|-|-|-|false", await RowOf(reading, vm.Id));
        Assert.NotNull(reloaded.Operation);
        Assert.NotNull(reading.Entry(reloaded).Reference(v => v.Operation).TargetEntry);
        Assert.Null(reloaded.Operation.PendingId);
        Assert.Equal(VmStatus.Running, reloaded.InstanceStatus);
    }

    // The VMs of one tenant in every shape the billed query must answer for, named by what they are.
    private static List<Vm> EveryShape(Guid tenantId)
    {
        Vm Resting(string name, VmStatus status)
        {
            Vm vm = NewVm(tenantId, name);
            vm.MarkRunning("ext-" + name, "10.0.0.5");
            vm.MarkObserved(status, "10.0.0.5", null);
            return vm;
        }

        Vm stopping = Resting("stopping-from-running", VmStatus.Running);
        stopping.Stop(Guid.NewGuid());
        Vm startingFromStopped = Resting("starting-from-stopped", VmStatus.Stopped);
        startingFromStopped.Start(Guid.NewGuid());
        Vm startingFromFrozen = Resting("starting-from-frozen", VmStatus.Frozen);
        startingFromFrozen.Start(Guid.NewGuid());
        Vm snapshottingRunning = Resting("snapshotting-running", VmStatus.Running);
        snapshottingRunning.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s");
        Vm snapshottingStopped = Resting("snapshotting-stopped", VmStatus.Stopped);
        snapshottingStopped.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s");
        Vm deleting = Resting("deleting-from-running", VmStatus.Running);
        deleting.Delete(Guid.NewGuid());
        Vm unknown = Resting("unknown", VmStatus.Running);
        unknown.Stop(Guid.NewGuid());
        unknown.StopSucceeded(unknown.Operation.PendingId!.Value, null, null, null);
        Vm notFound = Resting("not-found", VmStatus.Running);
        notFound.MarkNotFound();
        Vm gaveUp = NewVm(tenantId, "create-gave-up");
        gaveUp.CreateFailed(gaveUp.Operation.PendingId!.Value, null, "refused", null);

        return
        [
            Resting("running", VmStatus.Running), Resting("ready", VmStatus.Ready), Resting("stopped", VmStatus.Stopped),
            Resting("frozen", VmStatus.Frozen), Resting("error", VmStatus.Error),
            stopping, startingFromStopped, startingFromFrozen, snapshottingRunning, snapshottingStopped, deleting,
            unknown, notFound, gaveUp, NewVm(tenantId, "creating"),
        ];
    }

    private async Task<List<string>> BilledNamesOf(Guid tenantId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await new VmRepository(db).ListBillableAsync())
            .Where(v => v.TenantId == tenantId).Select(v => v.Name).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task TheBilledQuery_AnswersAsVmIsBilled_ForEveryShape()
    {
        Guid tenantId = TestTenant.WithRoomToSpare().Id;
        List<Vm> shapes = EveryShape(tenantId);
        List<string> expected = shapes.Where(v => v.IsBilled).Select(v => v.Name).OrderBy(n => n).ToList();
        Assert.Contains("starting-from-frozen", expected);
        Assert.DoesNotContain("starting-from-stopped", expected);
        Assert.DoesNotContain("create-gave-up", expected);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Vms.AddRange(shapes);
            await db.SaveChangesAsync();
        }

        Assert.Equal(expected, await BilledNamesOf(tenantId));
    }

    // Negative test: a row written when the status seen was kept at rest too holds it, equal to Status. The query and
    // the entity read the same answer from it as from a row written now.
    [Fact]
    public async Task ARowThatStillHoldsTheStatusSeenAtRest_ReadsWithTheSameAnswers()
    {
        Guid tenantId = TestTenant.WithRoomToSpare().Id;
        List<Vm> shapes = EveryShape(tenantId);
        List<string> expected = shapes.Where(v => v.IsBilled).Select(v => v.Name).OrderBy(n => n).ToList();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Vms.AddRange(shapes);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE core.vms SET observed_state = status WHERE tenant_id = {0} "
                + "AND status IN ('Running', 'Ready', 'Stopped', 'Frozen', 'Error')", tenantId);
        }

        Assert.Equal(expected, await BilledNamesOf(tenantId));
        await using TitanDbContext reading = pg.NewDbContext();
        List<Vm> reloaded = await reading.Vms.Where(v => v.TenantId == tenantId).ToListAsync();
        foreach (Vm vm in reloaded)
        {
            Vm shape = shapes.Single(s => s.Id == vm.Id);
            Assert.True(shape.IsBilled == vm.IsBilled, vm.Name);
            Assert.True(shape.InstanceStatus == vm.InstanceStatus, vm.Name);
        }
    }

    [Fact]
    public async Task TheNextWriteOfSuchARow_LeavesNothingOfTheOldShape()
    {
        Vm vm = NewVm(TestTenant.WithRoomToSpare().Id, "op-db-legacy");
        vm.MarkRunning("vm42", "10.0.0.5");
        vm.MarkObserved(VmStatus.Stopped, null, null);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Vms.Add(vm);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE core.vms SET observed_state = 'Stopped' WHERE id = {0}", vm.Id);
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Vm stored = await db.Vms.SingleAsync(v => v.Id == vm.Id);
            Assert.Equal(VmStatus.Stopped, stored.InstanceStatus);
            stored.MarkObserved(VmStatus.Running, "10.0.0.5", null);
            await db.SaveChangesAsync();
        }

        Assert.Equal("Running|false|false|-|-|-|false", await RowOf(vm.Id));
        await using TitanDbContext reading = pg.NewDbContext();
        Vm reloaded = await reading.Vms.SingleAsync(v => v.Id == vm.Id);
        Assert.Equal(VmStatus.Running, reloaded.InstanceStatus);
        Assert.True(reloaded.IsBilled);
    }
}

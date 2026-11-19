using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops architecture/resource-lifecycle.md: the view core.pending_requests and the
/// stuck-request query that joins it to the command row by the key the request names (§lost-and-stale), and the row
/// lock that makes two requests on one VM take turns (§one-key-per-request). Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class PendingRequestsDbTests(PostgresFixture pg)
{
    private const string Offering = "m-pending";
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    private async Task<Vm> StoredRunningVmAsync(int stopTimeoutSeconds = Vm.DefaultStopTimeoutSeconds)
    {
        Vm vm = StoredVm(stopTimeoutSeconds);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        await StoreAsync(vm);
        return vm;
    }

    private static Vm StoredVm(int stopTimeoutSeconds = Vm.DefaultStopTimeoutSeconds) =>
        Vm.Create(Tenant, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], Offering, "tpl", "cluster-a", 20,
            stopTimeoutSeconds: stopTimeoutSeconds);

    private async Task StoreAsync(Vm vm)
    {
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, vcpu_count, memory_mb, price_per_hour) VALUES ('{Offering}', 'm', 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
    }

    private async Task AgeTheRequestAsync(Guid vmId, int minutes)
    {
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE core.vms SET pending_request_asked_at = now() - make_interval(mins => {minutes}) WHERE id = {vmId}");
    }

    private async Task<Guid> StopThroughTheHandlerAsync(Guid vmId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        VmRepository vms = new(db);
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var handler = new StopVmCommandHandler(vms, caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new StopVmCommand(vmId));
        return (await vms.FindByIdAsync(vmId))!.Operation.PendingId!.Value;
    }

    [Fact]
    public async Task TheView_ListsOnlyVmsWithAWaitingRequest_WithItsIdKindAndTime()
    {
        Vm waiting = await StoredRunningVmAsync();
        Vm resting = await StoredRunningVmAsync();
        Guid stopId = await StopThroughTheHandlerAsync(waiting.Id);

        await using TitanDbContext db = pg.NewDbContext();
        List<string> rows = await db.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE id = {waiting.Id} OR id = {resting.Id}")
            .ToListAsync();

        Assert.Equal([$"vm|Stop|{stopId}"], rows);
    }

    [Fact]
    public async Task AStuckRequest_IsListedWithItsCommandRowsStatus_AndAYoungOneIsNot()
    {
        Vm stuckWithRow = await StoredRunningVmAsync();
        Guid stuckId = await StopThroughTheHandlerAsync(stuckWithRow.Id);
        await AgeTheRequestAsync(stuckWithRow.Id, 20);
        Vm young = await StoredRunningVmAsync();
        await StopThroughTheHandlerAsync(young.Id);
        Vm stuckWithoutRow = await StoredRunningVmAsync();
        Guid orphanId = await StopThroughTheHandlerAsync(stuckWithoutRow.Id);
        await AgeTheRequestAsync(stuckWithoutRow.Id, 20);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM core.provider_commands WHERE key = {ProviderCommandQueue.VmStopKey(stuckWithoutRow.Id, orphanId)}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest withRow = Assert.Single(stuck, s => s.Id == stuckWithRow.Id);
        Assert.Equal(("vm", stuckId, "Stop", "Pending", true), (withRow.Resource, withRow.RequestId, withRow.Kind, withRow.CommandStatus, withRow.CommandIsOpen));
        StuckRequest withoutRow = Assert.Single(stuck, s => s.Id == stuckWithoutRow.Id);
        Assert.Null(withoutRow.CommandStatus);
        Assert.False(withoutRow.CommandIsOpen);
        Assert.DoesNotContain(stuck, s => s.Id == young.Id);
    }

    private async Task<IReadOnlyList<StuckRequest>> StuckAfterFifteenMinutesAsync()
    {
        await using TitanDbContext read = pg.NewDbContext();
        return await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));
    }

    // titan-ops features/vm/design.md#vm-stop-timeout: a stop may wait its whole stop timeout for the guest, so it is stuck
    // only past the threshold plus the stop timeout its command carries.
    [Fact]
    public async Task AWaitingStop_IsStuckOnlyPastTheThresholdPlusTheStopTimeoutItsCommandCarries()
    {
        Vm vm = await StoredRunningVmAsync(stopTimeoutSeconds: Vm.MaxStopTimeoutSeconds);
        await StopThroughTheHandlerAsync(vm.Id);

        await AgeTheRequestAsync(vm.Id, 44);
        bool stuckAt44 = (await StuckAfterFifteenMinutesAsync()).Any(s => s.Id == vm.Id);
        await AgeTheRequestAsync(vm.Id, 46);
        bool stuckAt46 = (await StuckAfterFifteenMinutesAsync()).Any(s => s.Id == vm.Id);

        Assert.Equal((false, true), (stuckAt44, stuckAt46));
    }

    // The command's value, not the VM's: a change after the stop was recorded does not reach the worker's wait.
    // A command waits while the backup of its VM holds the VM (titan-ops features/provider/design.md#tenant-lane): that
    // wait is not stuck. Once the backup is done, the same request past the threshold is.
    [Fact]
    public async Task AStopWaitingBehindTheBackupOfItsVm_IsNotStuck_UntilTheBackupIsDone()
    {
        Vm vm = await StoredRunningVmAsync();
        await StopThroughTheHandlerAsync(vm.Id);
        await AgeTheRequestAsync(vm.Id, 20);
        ProviderCommand backup = ProviderCommand.Create($"vm-backup-{vm.Id}-20261119", ProviderCommandKinds.VmBackup,
            "cluster-a", "{}", DateTimeOffset.UtcNow.AddMinutes(-21), Tenant, vm.Id);
        Guid lease = Guid.NewGuid();
        backup.Lease(lease, DateTimeOffset.UtcNow);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.ProviderCommands.Add(backup);
            await db.SaveChangesAsync();
        }

        bool stuckDuringTheBackup = (await StuckAfterFifteenMinutesAsync()).Any(s => s.Id == vm.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            ProviderCommand stored = await db.ProviderCommands.SingleAsync(c => c.Id == backup.Id);
            stored.Complete(lease);
            await db.SaveChangesAsync();
        }
        bool stuckAfterTheBackup = (await StuckAfterFifteenMinutesAsync()).Any(s => s.Id == vm.Id);

        Assert.False(stuckDuringTheBackup);
        Assert.True(stuckAfterTheBackup);
    }

    [Fact]
    public async Task AStopTimeoutChangedAfterTheStop_DoesNotMoveTheThreshold()
    {
        Vm vm = await StoredRunningVmAsync(stopTimeoutSeconds: Vm.MaxStopTimeoutSeconds);
        await StopThroughTheHandlerAsync(vm.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.vms SET stop_timeout_seconds = {Vm.MinStopTimeoutSeconds} WHERE id = {vm.Id}");
        }

        await AgeTheRequestAsync(vm.Id, 20);

        Assert.DoesNotContain(await StuckAfterFifteenMinutesAsync(), s => s.Id == vm.Id);
    }

    // A command enqueued before its payload carried a stop timeout waits the worker's default, 120 s.
    [Fact]
    public async Task AStopCommandWithoutAStopTimeout_CountsTheWorkersDefault()
    {
        Vm vm = await StoredRunningVmAsync();
        Guid stopId = await StopThroughTheHandlerAsync(vm.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.provider_commands SET payload = payload - 'stopTimeoutSeconds' WHERE key = {ProviderCommandQueue.VmStopKey(vm.Id, stopId)}");
        }

        await AgeTheRequestAsync(vm.Id, 16);
        bool stuckAt16 = (await StuckAfterFifteenMinutesAsync()).Any(s => s.Id == vm.Id);
        await AgeTheRequestAsync(vm.Id, 18);
        bool stuckAt18 = (await StuckAfterFifteenMinutesAsync()).Any(s => s.Id == vm.Id);

        Assert.Equal((false, true), (stuckAt16, stuckAt18));
    }

    // Only a stop waits on the guest: a create of a VM with the longest stop timeout is stuck at the plain threshold.
    [Fact]
    public async Task AWaitingCreate_IsStuckAtThePlainThreshold_WhateverTheStopTimeout()
    {
        Vm creating = StoredVm(Vm.MaxStopTimeoutSeconds);
        await StoreAsync(creating);

        await AgeTheRequestAsync(creating.Id, 16);

        StuckRequest stuck = Assert.Single(await StuckAfterFifteenMinutesAsync(), s => s.Id == creating.Id);
        Assert.Equal("Create", stuck.Kind);
    }

    // titan-ops architecture/resource-lifecycle.md#one-key-per-request: the second request waits on the row lock, then sees the
    // first waiting and is a repeat — one request, one command row, neither scope fails.
    [Fact]
    public async Task TwoStops_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        Vm vm = await StoredRunningVmAsync();

        await Task.WhenAll(StopThroughTheHandlerAsync(vm.Id), StopThroughTheHandlerAsync(vm.Id));

        await using TitanDbContext db = pg.NewDbContext();
        Vm stored = await db.Vms.SingleAsync(v => v.Id == vm.Id);
        Assert.Equal(VmStatus.Stopping, stored.Status);
        Assert.NotNull(stored.Operation.PendingId);
        int rows = await db.Set<ProviderCommand>().CountAsync(c => c.Key.StartsWith($"vm-stop-{vm.Id}-"));
        Assert.Equal(1, rows);
    }
}

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

    private async Task<Vm> StoredRunningVmAsync()
    {
        Vm vm = Vm.Create(Tenant, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], Offering, "tpl", "cluster-a", 20);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('{Offering}', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return vm;
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
        return (await vms.FindByIdAsync(vmId))!.PendingOperationId!.Value;
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
        Assert.NotNull(stored.PendingOperationId);
        int rows = await db.Set<ProviderCommand>().CountAsync(c => c.Key.StartsWith($"vm-stop-{vm.Id}-"));
        Assert.Equal(1, rows);
    }
}

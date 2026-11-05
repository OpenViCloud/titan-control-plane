using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops architecture/resource-lifecycle.md for VM snapshots: the VM row lock that makes
/// two requests on one snapshot take turns (§one-key-per-request), and the stuck-request query joining a snapshot's request
/// to its command row (§lost-and-stale). Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VmSnapshotRequestsDbTests(PostgresFixture pg)
{
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    // A running VM with one snapshot Incus holds; nothing waits on either.
    private async Task<(Vm Vm, VmSnapshot Snapshot)> StoredVmWithASnapshotAsync()
    {
        string offeringId = await SeedOfferingAsync();
        Vm vm = Vm.Create(Tenant, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..12], offeringId, "tpl-1", "cluster-a", 20);
        vm.FinishOperation(vm.Pending!.Id, $"vm{Guid.NewGuid():N}"[..20], VmState.Running, "10.0.0.5", null);
        VmSnapshot snapshot = vm.WithSnapshot("nightly");
        vm.FinishSnapshotOperation(snapshot.Id, snapshot.Pending!.Id, $"{vm.ExternalId}/s1");
        await using TitanDbContext db = pg.NewDbContext();
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return (vm, snapshot);
    }

    private async Task<string> SeedOfferingAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        VmOffering? existing = await db.VmOfferings.FirstOrDefaultAsync();
        return existing?.Id ?? throw new InvalidOperationException("The migrated database seeds the VM offering catalog; none found.");
    }

    private async Task RevertThroughTheHandlerAsync(Guid vmId, Guid snapshotId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var handler = new RevertVmSnapshotCommandHandler(new VmRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new RevertVmSnapshotCommand(vmId, snapshotId));
    }

    private async Task DeleteThroughTheHandlerAsync(Guid vmId, Guid snapshotId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var handler = new DeleteVmSnapshotCommandHandler(new VmRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new DeleteVmSnapshotCommand(vmId, snapshotId));
    }

    private async Task<VmSnapshot> StoredSnapshotAsync(Guid vmId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        Vm stored = await db.Vms.Include(v => v.Snapshots).SingleAsync(v => v.Id == vmId);
        return stored.Snapshots.Single();
    }

    // The second request waits on the VM's row lock, then sees the first waiting and is a repeat: one request, one
    // command row, neither scope fails (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task TwoRevertsOfOneSnapshot_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        (Vm vm, VmSnapshot snapshot) = await StoredVmWithASnapshotAsync();

        await Task.WhenAll(RevertThroughTheHandlerAsync(vm.Id, snapshot.Id), RevertThroughTheHandlerAsync(vm.Id, snapshot.Id));

        VmSnapshot stored = await StoredSnapshotAsync(vm.Id);
        Assert.Equal(VmSnapshotOperationKind.Revert, stored.Pending!.Kind);
        await using TitanDbContext db = pg.NewDbContext();
        List<string> keys = await db.Set<ProviderCommand>()
            .Where(c => c.Key.StartsWith($"vm-snapshot-revert-{snapshot.Id}-")).Select(c => c.Key).ToListAsync();
        Assert.Equal([$"vm-snapshot-revert-{snapshot.Id}-{stored.Pending.Id}"], keys);
    }

    [Fact]
    public async Task TwoDeletesOfOneSnapshot_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        (Vm vm, VmSnapshot snapshot) = await StoredVmWithASnapshotAsync();

        await Task.WhenAll(DeleteThroughTheHandlerAsync(vm.Id, snapshot.Id), DeleteThroughTheHandlerAsync(vm.Id, snapshot.Id));

        VmSnapshot stored = await StoredSnapshotAsync(vm.Id);
        Assert.Equal(VmSnapshotOperationKind.Delete, stored.Pending!.Kind);
        await using TitanDbContext db = pg.NewDbContext();
        List<string> keys = await db.Set<ProviderCommand>()
            .Where(c => c.Key.StartsWith($"vm-snapshot-delete-{snapshot.Id}-")).Select(c => c.Key).ToListAsync();
        Assert.Equal([$"vm-snapshot-delete-{snapshot.Id}-{stored.Pending.Id}"], keys);
    }

    [Fact]
    public async Task AStuckSnapshotRequest_IsListedWithItsCommandRowsStatus()
    {
        (Vm vm, VmSnapshot snapshot) = await StoredVmWithASnapshotAsync();
        await RevertThroughTheHandlerAsync(vm.Id, snapshot.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.vm_snapshots SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE id = {snapshot.Id}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest revert = Assert.Single(stuck, s => s.Id == snapshot.Id);
        Assert.Equal(("vm-snapshot", "Revert", "Pending", true), (revert.Resource, revert.Kind, revert.CommandStatus, revert.CommandIsOpen));
    }
}

using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops architecture/resource-lifecycle.md for volumes: the row lock that makes two
/// requests on one volume take turns (§one-key-per-request), the stuck-request query joining a volume's request to its
/// command row (§lost-and-stale), and the locked list of a VM's volumes its delete releases. Category=Integration — its
/// own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VolumeRequestsDbTests(PostgresFixture pg)
{
    private const string VmOffering = "m-volreq";
    private const string VolumeOffering = "vol-volreq";
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    private async Task SeedOfferingsAsync(TitanDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('{VmOffering}', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.volume_offerings (id, name, size_gb, is_customized, price_per_gb_per_hour) VALUES ('{VolumeOffering}', 'v', 10, false, 0.0001) ON CONFLICT (id) DO NOTHING");
    }

    // A VM on Incus and a volume attached to it, nothing waiting on the volume; the VM waits on its delete when asked.
    private async Task<(Vm Vm, Volume Volume)> StoredAttachedVolumeAsync(bool vmDeleteWaits = false, bool released = true)
    {
        Vm vm = Vm.Create(Tenant, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], VmOffering, "tpl", "cluster-a", 20);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        Volume volume = Volume.Create(Tenant, $"vol-{Guid.NewGuid():N}"[..14], "cluster-a", VolumeOffering, 10);
        volume.MarkCreated($"vol-{volume.Id:N}");
        volume.Attach(vm);
        volume.MarkAttached(vm.Id);
        if (vmDeleteWaits)
        {
            vm.Delete(Guid.NewGuid());
            if (released)
            {
                volume.ReleaseFromVmDelete(vm.Id);
            }
        }
        await using TitanDbContext db = pg.NewDbContext();
        await SeedOfferingsAsync(db);
        db.Vms.Add(vm);
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();
        return (vm, volume);
    }

    private async Task DetachThroughTheHandlerAsync(Guid volumeId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var handler = new DetachVolumeCommandHandler(new VolumeRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new DetachVolumeCommand(volumeId));
    }

    // The second request waits on the row lock, then sees the first waiting and is a repeat: one request, one command row,
    // neither scope fails (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task TwoDetaches_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        (_, Volume volume) = await StoredAttachedVolumeAsync();

        await Task.WhenAll(DetachThroughTheHandlerAsync(volume.Id), DetachThroughTheHandlerAsync(volume.Id));

        await using TitanDbContext db = pg.NewDbContext();
        Volume stored = await db.Volumes.SingleAsync(v => v.Id == volume.Id);
        Assert.Equal(VolumeStatus.Detaching, stored.Status);
        int rows = await db.Set<ProviderCommand>().CountAsync(c => c.Key.StartsWith($"volume-detach-{volume.Id}-"));
        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task AStuckVolumeRequest_IsListedWithItsCommandRowsStatus()
    {
        (_, Volume volume) = await StoredAttachedVolumeAsync();
        await DetachThroughTheHandlerAsync(volume.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.volumes SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE id = {volume.Id}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest row = Assert.Single(stuck, s => s.Id == volume.Id);
        Assert.Equal(("volume", "Detach", "Pending", true), (row.Resource, row.Kind, row.CommandStatus, row.CommandIsOpen));
    }

    // The VM's delete Done reads the volumes it holds under their row lock (raw SQL that names the status word) and
    // leaves each one with nothing waiting at rest, unattached.
    [Fact]
    public async Task TheVmsDeleteDone_LeavesItsVolumeResting_Unattached()
    {
        (Vm vm, Volume volume) = await StoredAttachedVolumeAsync(vmDeleteWaits: true);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var handler = new EndVmRequestCommandHandler(new VmRepository(db), new VolumeRepository(db),
                new ActivityRecorder(new ActivityEventRepository(db), Callers.TenantMember(Tenant)));
            await handler.HandleAsync(new EndVmRequestCommand(vm.Id, vm.Operation.PendingId!.Value, "Done"));
        }

        await using TitanDbContext read = pg.NewDbContext();
        Volume stored = await read.Volumes.SingleAsync(v => v.Id == volume.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(VolumeStatus.Available, stored.Status);
        Assert.Null(stored.CurrentAttachedVmId);
        Assert.Null(stored.DesiredAttachedVmId);
    }

    // The VM's delete ending reads its volumes under their row lock, so it decides on what a volume request committed
    // first: here an attach of the customer's own, written while the row was held, which the ending then leaves alone.
    // Read without the lock, the ending would decide on the volume as it was and write its desired attachment over the
    // attach's.
    [Fact]
    public async Task TheVmsDeleteEnding_DecidesOnWhatAVolumeRequestHoldingTheRowCommitted()
    {
        (Vm vm, Volume volume) = await StoredAttachedVolumeAsync(vmDeleteWaits: true, released: false);
        Guid attach = Guid.NewGuid();
        await using TitanDbContext holder = pg.NewDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE core.volumes SET pending_request_id = {attach}, status = 'Attaching', pending_request_asked_at = now() WHERE id = {volume.Id}");

        Task ending = Task.Run(async () =>
        {
            await using TitanDbContext db = pg.NewDbContext();
            var handler = new EndVmRequestCommandHandler(new VmRepository(db), new VolumeRepository(db),
                new ActivityRecorder(new ActivityEventRepository(db), Callers.TenantMember(Tenant)));
            await handler.HandleAsync(new EndVmRequestCommand(vm.Id, vm.Operation.PendingId!.Value, "Done"));
        });
        await Task.Delay(TimeSpan.FromSeconds(1));
        await held.CommitAsync();
        await ending;

        await using TitanDbContext read = pg.NewDbContext();
        Volume stored = await read.Volumes.SingleAsync(v => v.Id == volume.Id);
        Assert.Equal((VolumeStatus.Attaching, (Guid?)attach), (stored.Status, stored.Operation.PendingId));
        Assert.Equal(vm.Id, stored.DesiredAttachedVmId);
        Assert.Null(stored.CurrentAttachedVmId);
    }
}

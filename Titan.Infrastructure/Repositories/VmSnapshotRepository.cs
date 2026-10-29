using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class VmSnapshotRepository(TitanDbContext db) : IVmSnapshotRepository
{
    public async Task<VmSnapshot?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.VmSnapshots.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<List<VmSnapshot>> FindByVmIdAsync(Guid vmId, CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.VmId == vmId && s.CurrentExistence != CurrentExistence.Deleted)
            .OrderByDescending(s => s.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    // Occupancy against MaxVmSnapshotsPerVm (titan-ops features/vm/design.md#vm-snapshot-counting): what Incus holds or is
    // about to, unless its delete was pressed.
    public async Task<int> CountActiveByVmIdAsync(Guid vmId, CancellationToken ct = default) =>
        await db.VmSnapshots.CountAsync(
            s => s.VmId == vmId
              && s.DesiredExistence == DesiredExistence.Exists
              && (s.CurrentExistence == CurrentExistence.Exists
                  || (s.CurrentExistence == CurrentExistence.NotCreated && s.PendingIntentId != null)), ct);

    // A waiting create or revert touches the VM on the provider. A snapshot at rest does not block VM deletion:
    // MarkAllDeletedByVmIdAsync sweeps it when the VM is reported Deleted. A waiting delete does not either: the worker's
    // delete no-ops once the VM is gone (titan-ops architecture/intent-and-observation.md#provider-delete-idempotency).
    public async Task<bool> AnyInProgressByVmIdAsync(Guid vmId, CancellationToken ct = default) =>
        await db.VmSnapshots.AnyAsync(
            s => s.VmId == vmId
              && s.PendingIntentId != null
              && s.DesiredExistence == DesiredExistence.Exists, ct);

    public async Task<List<VmSnapshot>> ListBillableAsync(CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.CurrentExistence == CurrentExistence.Exists)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<VmSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.ExternalId != null
                     && s.CurrentExistence != CurrentExistence.Deleted
                     && db.Vms.Any(v => v.Id == s.VmId && v.ClusterId == clusterId))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task SaveAsync(VmSnapshot snapshot, CancellationToken ct = default)
    {
        if (db.Entry(snapshot).State == EntityState.Detached)
            db.VmSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }

    // The VM's delete command confirmed it gone, and Incus deletes an instance's snapshots with it: an observation, it
    // writes the existence and ends whatever request was waiting.
    public async Task MarkAllDeletedByVmIdAsync(Guid vmId, CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.VmId == vmId && s.CurrentExistence != CurrentExistence.Deleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CurrentExistence, CurrentExistence.Deleted)
                .SetProperty(x => x.PendingIntentId, (Guid?)null), ct);
}

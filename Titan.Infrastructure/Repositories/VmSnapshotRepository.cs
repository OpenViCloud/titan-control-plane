using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Read side of the snapshot table (titan-ops architecture/code-structure.md#child-entity); writes go through VmRepository.
public class VmSnapshotRepository(TitanDbContext db) : IVmSnapshotRepository
{
    public async Task<List<VmSnapshot>> FindByVmIdAsync(Guid vmId, CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.VmId == vmId && s.CurrentExistence != CurrentExistence.Deleted)
            .OrderByDescending(s => s.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<VmSnapshot>> ListBillableAsync(CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.CurrentExistence == CurrentExistence.Exists)
            .AsNoTracking()
            .ToListAsync(ct);

    // A snapshot has no cluster of its own; it lives where its VM lives.
    public async Task<List<ReconcilableVmSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.ExternalId != null
                     && s.CurrentExistence != CurrentExistence.Deleted
                     && db.Vms.Any(v => v.Id == s.VmId && v.ClusterId == clusterId))
            // The filter above keeps only rows with an id.
            .Select(s => new ReconcilableVmSnapshot(s.Id, s.VmId, s.ExternalId!, s.CurrentExistence))
            .ToListAsync(ct);
}

using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Read side of the snapshot table (titan-ops architecture/code-structure.md#child-entity); writes go through VmRepository.
/// Every state condition is the SQL twin of a plain bool on VmSnapshot (titan-ops architecture/domain-methods.md#plain-code).
public class VmSnapshotRepository(TitanDbContext db) : IVmSnapshotRepository
{
    // VmSnapshot.IsNotDeleted
    public async Task<List<VmSnapshot>> FindByVmIdAsync(Guid vmId, CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.VmId == vmId && s.CurrentExistence != CurrentExistence.Deleted)
            .OrderByDescending(s => s.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    // VmSnapshot.IsBilled
    public async Task<List<VmSnapshot>> ListBillableAsync(CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.CurrentExistence == CurrentExistence.Exists)
            .AsNoTracking()
            .ToListAsync(ct);

    // VmSnapshot.IsNotDeleted with a provider id. A snapshot has no cluster of its own; it lives where its VM lives.
    public async Task<List<VmSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.VmSnapshots
            .Where(s => s.ExternalId != null
                     && s.CurrentExistence != CurrentExistence.Deleted
                     && db.Vms.Any(v => v.Id == s.VmId && v.ClusterId == clusterId))
            .AsNoTracking()
            .ToListAsync(ct);
}

using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Read side of the snapshot table (titan-ops architecture/code-structure.md#child-entity); writes go through VolumeRepository.
public class VolumeSnapshotRepository(TitanDbContext db) : IVolumeSnapshotRepository
{
    public async Task<List<VolumeSnapshot>> FindByVolumeIdAsync(Guid volumeId, CancellationToken ct = default) =>
        await db.VolumeSnapshots
            .Where(s => s.VolumeId == volumeId && s.CurrentExistence != CurrentExistence.Deleted)
            .OrderByDescending(s => s.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<VolumeSnapshot>> ListBillableAsync(CancellationToken ct = default) =>
        await db.VolumeSnapshots
            .Where(s => s.CurrentExistence == CurrentExistence.Exists)
            .AsNoTracking()
            .ToListAsync(ct);

    // A snapshot has no cluster of its own; it lives where its volume lives.
    public async Task<List<ReconcilableVolumeSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.VolumeSnapshots
            .Where(s => s.ExternalId != null
                     && s.CurrentExistence != CurrentExistence.Deleted
                     && db.Volumes.Any(v => v.Id == s.VolumeId && v.ClusterId == clusterId))
            // The filter above keeps only rows with an id.
            .Select(s => new ReconcilableVolumeSnapshot(s.Id, s.VolumeId, s.ExternalId!, s.CurrentExistence))
            .ToListAsync(ct);
}

using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class VolumeSnapshotRepository(TitanDbContext db) : IVolumeSnapshotRepository
{
    public async Task<VolumeSnapshot?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.VolumeSnapshots.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<List<VolumeSnapshot>> FindByVolumeIdAsync(Guid volumeId, CancellationToken ct = default) =>
        await db.VolumeSnapshots
            .Where(s => s.VolumeId == volumeId && s.CurrentExistence != CurrentExistence.Deleted)
            .OrderByDescending(s => s.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

    // Occupancy, for the per-volume quota and VOLUME_HAS_SNAPSHOTS alike
    // (titan-ops features/volume/design.md#counting-and-billing).
    public async Task<int> CountActiveByVolumeIdAsync(Guid volumeId, CancellationToken ct = default) =>
        await db.VolumeSnapshots.CountAsync(
            s => s.VolumeId == volumeId
                 && s.DesiredExistence == DesiredExistence.Exists
                 && (s.CurrentExistence == CurrentExistence.Exists
                     || (s.CurrentExistence == CurrentExistence.NotCreated && s.PendingIntentId != null)), ct);

    public async Task<List<VolumeSnapshot>> ListBillableAsync(CancellationToken ct = default) =>
        await db.VolumeSnapshots
            .Where(s => s.CurrentExistence == CurrentExistence.Exists)
            .AsNoTracking()
            .ToListAsync(ct);

    // A snapshot has no cluster of its own; it lives where its volume lives.
    public async Task<List<VolumeSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.VolumeSnapshots
            .Where(s => s.ExternalId != null
                     && s.CurrentExistence != CurrentExistence.Deleted
                     && db.Volumes.Any(v => v.Id == s.VolumeId && v.ClusterId == clusterId))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<VolumeSnapshot>> ListLiveOfVolumeForUpdateAsync(Guid volumeId, CancellationToken ct = default) =>
        await db.VolumeSnapshots
            .Where(s => s.VolumeId == volumeId && s.CurrentExistence != CurrentExistence.Deleted)
            .ToListAsync(ct);

    public async Task SaveAsync(VolumeSnapshot snapshot, CancellationToken ct = default)
    {
        if (db.Entry(snapshot).State == EntityState.Detached)
            db.VolumeSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }
}

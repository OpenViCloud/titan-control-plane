using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class VolumeRepository(TitanDbContext db) : IVolumeRepository
{
    public async Task<Volume?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Volumes.FirstOrDefaultAsync(v => v.Id == id, ct);

    public async Task<List<Volume>> ListAttachedToVmAsync(Guid vmId, CancellationToken ct = default) =>
        await db.Volumes
            .Where(v => (v.CurrentAttachedVmId == vmId || v.DesiredAttachedVmId == vmId)
                        && v.CurrentExistence != CurrentExistence.Deleted)
            .ToListAsync(ct);

    public async Task<List<Volume>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Volumes
            .Where(v => v.TenantId == tenantId && v.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);

    // Occupancy (titan-ops features/volume/design.md#counting-and-billing): what Incus holds or is about to, unless its
    // delete was pressed. A create that failed before Incus had it, or a volume Incus no longer has, does not count.
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Volumes.CountAsync(
            v => v.TenantId == tenantId
                 && v.DesiredExistence == DesiredExistence.Exists
                 && (v.CurrentExistence == CurrentExistence.Exists
                     || (v.CurrentExistence == CurrentExistence.NotCreated && v.PendingIntentId != null)), ct);

    public async Task<List<Volume>> ListBillableAsync(CancellationToken ct = default) =>
        await db.Volumes
            .Where(v => v.CurrentExistence == CurrentExistence.Exists)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<Volume>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Volumes
            .Where(v => v.ClusterId == clusterId && v.ExternalId != null && v.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(Volume volume) => db.Volumes.Add(volume);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

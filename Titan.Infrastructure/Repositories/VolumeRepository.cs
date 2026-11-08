using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

// Every state condition below is the SQL twin of a plain bool on Volume (titan-ops architecture/domain-methods.md#plain-code);
// VolumeRepositoryStateTests runs every shape through both, and QueryRuleDbTests pins each side of the quota and billing
// conditions on Postgres.
public class VolumeRepository(TitanDbContext db) : IVolumeRepository
{
    public async Task<Volume?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Volumes.Include(v => v.Snapshots).FirstOrDefaultAsync(v => v.Id == id, ct);

    // Lock first, load second, as NetworkRepository.FindByIdForUpdateAsync (titan-ops architecture/code-structure.md#child-entity).
    public async Task<Volume?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.volumes WHERE id = {id} FOR UPDATE", ct);
        return await FindByIdAsync(id, ct);
    }

    // !Volume.IsDeleted. Lock first, load second, as FindByIdForUpdateAsync; the lock statement filters as the load does.
    public async Task<List<Volume>> ListAttachedToVmLockedAsync(Guid vmId, CancellationToken ct = default)
    {
        await db.LockRowAsync(
            $"SELECT id FROM core.volumes WHERE (current_attached_vm_id = {vmId} OR desired_attached_vm_id = {vmId}) AND status <> 'Deleted' FOR UPDATE",
            ct);
        return await db.Volumes
            .Include(v => v.Snapshots)
            .Where(v => (v.CurrentAttachedVmId == vmId || v.DesiredAttachedVmId == vmId)
                        && v.Status != VolumeStatus.Deleted)
            .ToListAsync(ct);
    }

    // !Volume.IsDeleted
    public async Task<List<Volume>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Volumes
            .Where(v => v.TenantId == tenantId && v.Status != VolumeStatus.Deleted)
            .AsNoTracking()
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);

    // Volume.OccupiesQuota
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Volumes.CountAsync(
            v => v.TenantId == tenantId
                 && (v.Status == VolumeStatus.Creating
                     || v.Status == VolumeStatus.Available
                     || ((v.Status == VolumeStatus.Attaching || v.Status == VolumeStatus.Detaching)
                         && v.Operation.LastSeenStatus == VolumeStatus.Available)), ct);

    // Volume.IsBilled
    public async Task<List<Volume>> ListBillableAsync(CancellationToken ct = default) =>
        await db.Volumes
            .Where(v => v.Status == VolumeStatus.Available
                     || ((v.Status == VolumeStatus.Creating
                          || v.Status == VolumeStatus.Attaching
                          || v.Status == VolumeStatus.Detaching
                          || v.Status == VolumeStatus.Deleting)
                         && v.Operation.LastSeenStatus == VolumeStatus.Available))
            .AsNoTracking()
            .ToListAsync(ct);

    // !Volume.IsDeleted with a provider id.
    public async Task<List<Volume>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Volumes
            .Where(v => v.ClusterId == clusterId && v.ExternalId != null && v.Status != VolumeStatus.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(Volume volume) => db.Volumes.Add(volume);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

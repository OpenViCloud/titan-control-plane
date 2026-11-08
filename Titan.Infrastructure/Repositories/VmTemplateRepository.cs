using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class VmTemplateRepository(TitanDbContext db) : IVmTemplateRepository
{
    public async Task<VmTemplate?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.VmTemplates.Include(t => t.Clusters).FirstOrDefaultAsync(t => t.Id == id, ct);

    // The template's row, not its cluster rows: a cluster row a sync-all adds does not exist yet to be locked.
    public async Task<VmTemplate?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.vm_templates WHERE id = {id} FOR UPDATE", ct);
        return await FindByIdAsync(id, ct);
    }

    // Both synced lists write VmTemplateCluster.IsSynced in SQL. Unordered: ListTemplatesQueryHandler owns the
    // customer-facing name ordering.
    public async Task<List<VmTemplate>> ListSyncedInClusterAsync(string clusterId, CancellationToken ct = default) =>
        await db.VmTemplates
            .Include(t => t.Clusters)
            .Where(t => t.Clusters.Any(z => z.ClusterId == clusterId
                && (z.Status == VmTemplateClusterStatus.Available
                    || (z.Status == VmTemplateClusterStatus.Syncing && z.Operation.LastSeenStatus == VmTemplateClusterStatus.Available))))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<VmTemplate>> ListSyncedInAnyClusterAsync(CancellationToken ct = default) =>
        await db.VmTemplates
            .Include(t => t.Clusters)
            .Where(t => t.Clusters.Any(z => z.Status == VmTemplateClusterStatus.Available
                || (z.Status == VmTemplateClusterStatus.Syncing && z.Operation.LastSeenStatus == VmTemplateClusterStatus.Available)))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<VmTemplate>> ListReconcilableInClusterAsync(string clusterId, CancellationToken ct = default) =>
        await db.VmTemplates
            .Include(t => t.Clusters)
            .Where(t => t.Clusters.Any(z => z.ClusterId == clusterId && z.ExternalTemplateId != null))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<VmTemplate>> ListAllAsync(CancellationToken ct = default) =>
        await db.VmTemplates
            .Include(t => t.Clusters)
            .OrderBy(t => t.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(VmTemplate template) => db.VmTemplates.Add(template);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

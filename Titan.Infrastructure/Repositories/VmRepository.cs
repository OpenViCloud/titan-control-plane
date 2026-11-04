using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class VmRepository(TitanDbContext db) : IVmRepository
{
    public async Task<Vm?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Vms.Include(v => v.Snapshots).FirstOrDefaultAsync(v => v.Id == id, ct);

    // Lock first, load second, as NetworkRepository.FindByIdForUpdateAsync (titan-ops architecture/code-structure.md#child-entity).
    public async Task<Vm?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.vms WHERE id = {id} FOR UPDATE", ct);
        return await FindByIdAsync(id, ct);
    }

    public async Task<List<Vm>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.TenantId == tenantId && v.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Vms.Where(v => v.TenantId == tenantId).CountAsync(Vm.OccupiesQuota, ct);

    // The same condition as the unique index's filter (VmConfiguration): a narrower check would let the index 500.
    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.Vms.AnyAsync(
            v => v.TenantId == tenantId &&
                 v.Name == name &&
                 v.CurrentExistence != CurrentExistence.Deleted, ct);

    // Every VM Incus may hold, a create that gave up included: an instance that runs is billed whether or not its
    // create ended well (titan-ops architecture/resource-lifecycle.md#reconciler).
    public async Task<List<Vm>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.ClusterId == clusterId
                        && v.ExternalId != null
                        && v.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<Vm>> ListBillableAsync(CancellationToken ct = default) =>
        await db.Vms
            .Where(Vm.IsBilled)
            .AsNoTracking()
            .ToListAsync(ct);

    // titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read. tenantId == null spans all tenants.
    public async Task<List<Vm>> ListAllAsync(Guid? tenantId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.CurrentExistence != CurrentExistence.Deleted && (tenantId == null || v.TenantId == tenantId))
            .OrderBy(v => v.TenantId).ThenBy(v => v.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<Vm>> ListByExternalIdsAsync(
        Guid tenantId, IReadOnlyCollection<string> externalIds, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.TenantId == tenantId && v.ExternalId != null && externalIds.Contains(v.ExternalId))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<Vm>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => ids.Contains(v.Id))
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(Vm vm) => db.Vms.Add(vm);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

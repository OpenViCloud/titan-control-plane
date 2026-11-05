using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

// Every state condition below is the SQL twin of a plain bool on Vm (titan-ops architecture/domain-methods.md#plain-code);
// VmRepositoryStateTests runs every shape a VM can be in through both, and QueryRuleDbTests pins each side of the quota
// and billing conditions on Postgres.
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

    // !Vm.IsDeleted
    public async Task<List<Vm>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.TenantId == tenantId && v.Status != VmStatus.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Vm.OccupiesQuota
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Vms.CountAsync(
            v => v.TenantId == tenantId
                 && v.Status != VmStatus.Deleting
                 && v.Status != VmStatus.Deleted
                 && v.Status != VmStatus.NotFound
                 && v.Status != VmStatus.Failed, ct);

    // !Vm.IsDeleted — the same condition as the unique index's filter (VmConfiguration): a narrower check would let
    // the index 500.
    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.Vms.AnyAsync(
            v => v.TenantId == tenantId &&
                 v.Name == name &&
                 v.Status != VmStatus.Deleted, ct);

    // !Vm.IsDeleted with a provider id: every VM Incus may hold, a create that gave up included — an instance that
    // runs is billed whether or not its create ended well (titan-ops architecture/resource-lifecycle.md#reconciler).
    public async Task<List<Vm>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.ClusterId == clusterId
                        && v.ExternalId != null
                        && v.Status != VmStatus.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Vm.IsBilled
    public async Task<List<Vm>> ListBillableAsync(CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.Status != VmStatus.Deleted
                        && v.Status != VmStatus.NotFound
                        && (v.ObservedState == VmStatus.Running
                            || v.ObservedState == VmStatus.Ready
                            || v.ObservedState == VmStatus.Frozen
                            || v.ObservedState == VmStatus.Error))
            .AsNoTracking()
            .ToListAsync(ct);

    // !Vm.IsDeleted. titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read: tenantId == null spans all tenants.
    public async Task<List<Vm>> ListAllAsync(Guid? tenantId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.Status != VmStatus.Deleted && (tenantId == null || v.TenantId == tenantId))
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

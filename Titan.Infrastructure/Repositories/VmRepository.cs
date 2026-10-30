using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class VmRepository(TitanDbContext db) : IVmRepository
{
    public async Task<Vm?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Vms.FirstOrDefaultAsync(v => v.Id == id, ct);

    public async Task<List<Vm>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.TenantId == tenantId && v.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Quota counts what occupies the provider or is about to (titan-ops features/vm/design.md#counting-and-billing):
    // the instance exists, or its create is still waiting. A failed create, an instance not found, and a VM
    // whose delete was asked do not count.
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Vms.CountAsync(
            v => v.TenantId == tenantId &&
                 v.DesiredExistence == DesiredExistence.Exists &&
                 (v.CurrentExistence == CurrentExistence.Exists
                  || (v.CurrentExistence == CurrentExistence.NotCreated && v.PendingIntentId != null)), ct);

    // The same condition as the unique index's filter (VmConfiguration): a narrower check would let the index 500.
    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.Vms.AnyAsync(
            v => v.TenantId == tenantId &&
                 v.Name == name &&
                 v.CurrentExistence != CurrentExistence.Deleted, ct);

    public async Task<List<Vm>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.ClusterId == clusterId
                        && v.ExternalId != null
                        && v.CurrentExistence != CurrentExistence.Deleted
                        && (v.HasBeenRunning || v.RequestFailureReason == null))
            .AsNoTracking()
            .ToListAsync(ct);

    // A VM is billed while its instance holds CPU and RAM (titan-ops features/vm/design.md#counting-and-billing): every
    // Incus status but Stopped keeps the qemu process alive (incus driver_qemu.go statusCode).
    public async Task<List<Vm>> ListBillableAsync(CancellationToken ct = default) =>
        await db.Vms
            .Where(v => v.CurrentExistence == CurrentExistence.Exists
                        && (v.CurrentState == VmState.Running
                            || v.CurrentState == VmState.Ready
                            || v.CurrentState == VmState.Frozen
                            || v.CurrentState == VmState.Error))
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

using Titan.Domain.Aggregates.Vm;

namespace Titan.Domain.Repositories;

public interface IVmRepository
{
    /// The aggregate whole: the VM with its snapshots, tracked (titan-ops architecture/code-structure.md#child-entity).
    Task<Vm?> FindByIdAsync(Guid id, CancellationToken ct = default);
    /// The same, with the VM's row locked until this context's next save commits: the per-VM snapshot cap is counted
    /// on the loaded snapshots, so two requests adding a snapshot at the cap take turns.
    Task<Vm?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default);
    /// The VM a snapshot belongs to, with its snapshots, tracked; null when no VM has the snapshot. For the worker's
    /// callbacks, which name the snapshot.
    Task<Vm?> FindBySnapshotIdAsync(Guid snapshotId, CancellationToken ct = default);
    Task<List<Vm>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    /// One cluster's VMs the pool reconciler observes: with a provider id and not Deleted. A VM whose create gave up
    /// and that never ran stays out: a create that gave up must not turn Running.
    Task<List<ReconcilableVm>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    /// All tenants, not scoped to a caller: the compute meter is a platform-wide background job.
    Task<List<Vm>> ListBillableAsync(CancellationToken ct = default);
    /// Admin cross-tenant read (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read): every non-Deleted
    /// VM, or one tenant's when tenantId is given. Not scoped to a caller; ListByTenantAsync stays the
    /// tenant path.
    Task<List<Vm>> ListAllAsync(Guid? tenantId, CancellationToken ct = default);
    /// The tenant's VMs with these provider ids (Incus instance names), whatever their state. Only that tenant: the
    /// storage callback resolves the instances using a volume, and must never match another tenant's VM.
    Task<List<Vm>> ListByExternalIdsAsync(Guid tenantId, IReadOnlyCollection<string> externalIds, CancellationToken ct = default);
    /// The VMs with these ids, any tenant: the storage reconciler's list names the VM a volume is attached to by its
    /// Incus instance name.
    Task<List<Vm>> ListByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    void Add(Vm vm);
    Task SaveAsync(CancellationToken ct = default);
}

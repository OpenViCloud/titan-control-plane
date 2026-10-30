using Titan.Domain.Aggregates.Volume;

namespace Titan.Domain.Repositories;

public interface IVolumeRepository
{
    Task<Volume?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<Volume>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// All tenants: Incus holds it — including while a delete waits, until Incus has deleted it
    /// (titan-ops features/volume/design.md#counting-and-billing). Independent of any VM's state, by design.
    Task<List<Volume>> ListBillableAsync(CancellationToken ct = default);
    /// Volumes in a cluster the storage reconciler should observe: they have a provider id and are not Deleted.
    Task<List<Volume>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    void Add(Volume volume);
    /// Volumes a VM holds or was asked to hold (attached, attaching, detaching), not yet deleted.
    Task<List<Volume>> ListAttachedToVmAsync(Guid vmId, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

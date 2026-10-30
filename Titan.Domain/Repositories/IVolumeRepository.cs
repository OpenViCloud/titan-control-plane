using Titan.Domain.Aggregates.Volume;

namespace Titan.Domain.Repositories;

public interface IVolumeRepository
{
    /// The aggregate whole: the volume with its snapshots, tracked (titan-ops architecture/code-structure.md#child-entity).
    Task<Volume?> FindByIdAsync(Guid id, CancellationToken ct = default);
    /// The same, with the volume's row locked until this context's next save commits: the per-volume snapshot cap is
    /// counted on the loaded snapshots, so two requests adding a snapshot at the cap take turns.
    Task<Volume?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default);
    /// The volume a snapshot belongs to, with its snapshots, tracked; null when no volume has the snapshot. For the
    /// worker's callbacks, which name the snapshot.
    Task<Volume?> FindBySnapshotIdAsync(Guid snapshotId, CancellationToken ct = default);
    Task<List<Volume>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// All tenants: Incus holds it — including while a delete waits, until Incus has deleted it
    /// (titan-ops features/volume/design.md#counting-and-billing). Independent of any VM's state, by design.
    Task<List<Volume>> ListBillableAsync(CancellationToken ct = default);
    /// Volumes in a cluster the storage reconciler should observe: they have a provider id and are not Deleted.
    Task<List<Volume>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    void Add(Volume volume);
    /// Volumes a VM holds or was asked to hold (attached, attaching, detaching), not yet deleted; tracked, with their
    /// snapshots, since the VM's delete changes them.
    Task<List<Volume>> ListAttachedToVmAsync(Guid vmId, CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}

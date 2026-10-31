using Titan.Domain.Aggregates.Volume;

namespace Titan.Domain.Repositories;

/// Read side only (titan-ops architecture/code-structure.md#child-entity): a snapshot is written through its volume
/// (IVolumeRepository), which loads and saves the aggregate whole. These lists read the snapshot table directly so the
/// customer's list, the meter and the reconciler's pool do not load every volume.
public interface IVolumeSnapshotRepository
{
    Task<List<VolumeSnapshot>> FindByVolumeIdAsync(Guid volumeId, CancellationToken ct = default);
    /// All tenants: Incus holds it — including while a delete waits (titan-ops features/volume/design.md#counting-and-billing).
    Task<List<VolumeSnapshot>> ListBillableAsync(CancellationToken ct = default);
    /// Snapshots of volumes in a cluster that the storage reconciler should observe: provider id, not Deleted.
    Task<List<ReconcilableVolumeSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
}

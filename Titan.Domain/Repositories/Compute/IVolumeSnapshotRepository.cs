using Titan.Domain.Aggregates.Compute.VolumeSnapshot;

namespace Titan.Domain.Repositories.Compute;

public interface IVolumeSnapshotRepository
{
    Task<VolumeSnapshot?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<VolumeSnapshot>> FindByVolumeIdAsync(Guid volumeId, CancellationToken ct = default);
    Task<int> CountActiveByVolumeIdAsync(Guid volumeId, CancellationToken ct = default);
    /// All tenants: Incus holds it — including while a delete waits (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#counting-and-billing).
    Task<List<VolumeSnapshot>> ListBillableAsync(CancellationToken ct = default);
    /// Snapshots of volumes in a cluster that the storage reconciler should observe: provider id, not Deleted.
    Task<List<VolumeSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    /// The volume's snapshots not yet Deleted, tracked: the volume's Deleted report marks them, Incus having deleted
    /// them with it.
    Task<List<VolumeSnapshot>> ListLiveOfVolumeForUpdateAsync(Guid volumeId, CancellationToken ct = default);
    Task SaveAsync(VolumeSnapshot snapshot, CancellationToken ct = default);
}

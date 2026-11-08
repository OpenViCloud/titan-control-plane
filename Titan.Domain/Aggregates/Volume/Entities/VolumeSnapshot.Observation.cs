namespace Titan.Domain.Aggregates.Volume;

public partial class VolumeSnapshot
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): the status at rest, or only
    // what is seen while an operation waits; never throws, ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler found the snapshot on Incus. That proves it exists, not that a create finished.
    internal void MarkObserved()
    {
        if (IsDeleted || Status == VolumeSnapshotStatus.Failed) return;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(VolumeSnapshotStatus.Available);
        }
        else
        {
            Status = VolumeSnapshotStatus.Available;
        }
    }

    /// The reconciler did not find the snapshot, or its volume. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsDeleted || Status == VolumeSnapshotStatus.Failed) return;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(null);
        }
        else
        {
            Status = VolumeSnapshotStatus.NotFound;
        }
    }
}

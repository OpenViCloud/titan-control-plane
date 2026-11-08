namespace Titan.Domain.Aggregates.Volume;

/// The one status of a volume snapshot, the only one the API shows (titan-ops features/volume/design.md#volume-snapshot).
/// Incus has no status word for a volume snapshot, only whether it is there: two say an operation is waiting, Available
/// and NotFound say whether Incus holds it at rest, and the last two say what is not a resting snapshot.
public enum VolumeSnapshotStatus
{
    Creating,
    Deleting,
    // Incus holds the snapshot and nothing waits: the only status a restore starts from.
    Available,
    // The reconciler did not find the snapshot or its volume; never Deleted, which only a delete writes, never an observation.
    NotFound,
    // The create gave up before Incus held a snapshot.
    Failed,
    Deleted
}

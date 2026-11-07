namespace Titan.Domain.Aggregates.Vm;

/// The one status of a VM snapshot, the only one the API shows (titan-ops features/vm/design.md#vm-snapshot). Incus has no
/// status word for an instance snapshot, only whether it is there: three say an operation is waiting, Available and
/// NotFound say whether Incus holds it at rest, and the last two say what is not a resting snapshot.
public enum VmSnapshotStatus
{
    Creating,
    Reverting,
    Deleting,
    // Incus holds the snapshot and nothing waits: the only status a revert starts from.
    Available,
    // The reconciler did not find the snapshot on its VM; never Deleted, which only a delete writes, never an observation.
    NotFound,
    // The create gave up before Incus held a snapshot.
    Failed,
    Deleted
}

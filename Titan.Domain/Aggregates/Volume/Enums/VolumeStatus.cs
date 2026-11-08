namespace Titan.Domain.Aggregates.Volume;

/// The one status of a volume, the only one the API shows (titan-ops features/volume/design.md#volume-state-is-existence).
/// Incus has no status word for a custom volume, only whether it is there and used by instances or not: four say an
/// operation is waiting, Available and NotFound say whether Incus holds it at rest, and the last two say what is not a
/// resting volume. Which instance uses it is not a status: the attachment stays three fields.
public enum VolumeStatus
{
    Creating,
    Attaching,
    Detaching,
    Deleting,
    // Incus holds the volume and nothing waits: the only status an attach, a detach or a snapshot starts from.
    Available,
    // The reconciler did not find the volume; never Deleted, which only a delete writes, never an observation.
    NotFound,
    // The create gave up before Incus held a volume.
    Failed,
    Deleted
}

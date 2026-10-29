namespace Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;

// The worker's callback payload: an observation, but it writes, so it is a command. The route carries both ids.
//
// Report says which of four reports this is (titan-ops features/volume/design.md#volume-reports): Observed (the storage
// reconciler: Exists), Created and Deleted (the create and delete commands; Created brings the id), and a command's
// give-up (Reason/Detail; the create's carries the id when Incus has the snapshot).
public record UpdateVolumeSnapshotStatusCommand(
    Guid VolumeId,
    Guid SnapshotId,
    string Report,
    bool? Exists = null,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

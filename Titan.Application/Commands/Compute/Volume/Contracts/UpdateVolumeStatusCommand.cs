namespace Titan.Application.Commands.Compute.Volume.Contracts;

// The worker's callback payload: an observation, but it writes, so it is a command. VolumeId comes from the route.
//
// Report says which of four reports this is (titan-ops features/volume/design.md#volume-reports): Observed (the storage
// reconciler, or an attach or detach command: Exists, and AttachedTo — the names of the instances Incus lists in the
// volume's used_by — when it exists), Created and Deleted (the create/restore and delete commands; Created brings the
// id), and a command's give-up (Reason/Detail; the create's carries the id when Incus has the volume, an attach's or
// detach's what Incus showed).
public record UpdateVolumeStatusCommand(
    Guid VolumeId,
    string Report,
    bool? Exists = null,
    List<string>? AttachedTo = null,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

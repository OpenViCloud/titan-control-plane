namespace Titan.Application.Commands.Compute.Volume.Contracts;

// The report of a volume request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes). Both ids
// come from the route: the request id is the one the command carries. Outcome is "Done" or "Failed"; AttachedTo is
// present when the worker read the volume on Incus: the instances in its used_by, none when it is free.
public record EndVolumeRequestCommand(
    Guid VolumeId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    List<string>? AttachedTo = null,
    string? Reason = null,
    string? Detail = null);

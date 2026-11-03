namespace Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;

// The report of a volume snapshot request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes);
// the three ids come from the route. Outcome is "Done" or "Failed".
public record EndVolumeSnapshotRequestCommand(
    Guid VolumeId,
    Guid SnapshotId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

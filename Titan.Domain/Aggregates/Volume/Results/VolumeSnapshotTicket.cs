namespace Titan.Domain.Aggregates.Volume;

/// The new snapshot and the provider id of its volume, handed out together past the volume's own check
/// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public sealed record VolumeSnapshotTicket(VolumeSnapshot Snapshot, string ExternalVolumeId);

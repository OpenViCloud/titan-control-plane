namespace Titan.Domain.Aggregates.Volume;

/// What Volume.MarkDeleted did, in one answer: whether the report fulfilled the volume's own waiting delete, and
/// which of its snapshots had a delete of their own waiting that the volume's delete finished for them. One call
/// tells the callback both, so it cannot record the volume's outcome and forget the snapshots'.
public record VolumeDeletionOutcome(bool FulfilledVolumeDelete, IReadOnlyList<VolumeSnapshot> SnapshotsWhoseDeleteWasFulfilled);

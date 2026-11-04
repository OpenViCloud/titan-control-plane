using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// What Volume.EndOperation ended, in one answer: the operation the report named, and — when a delete ended — the
/// snapshots whose own delete was waiting and went with the volume (titan-ops architecture/resource-lifecycle.md#shape).
/// The handler records each outcome once from this.
public sealed record VolumeOperationEnded(
    PendingOperation<VolumeOperationKind> Operation,
    IReadOnlyList<VolumeSnapshot> SnapshotsWhoseDeleteEnded);

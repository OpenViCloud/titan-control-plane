using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// What Volume.EndRequest ended, in one answer: the request the report named, and — when a delete ended — the
/// snapshots whose own delete was waiting and went with the volume (titan-ops architecture/resource-lifecycle.md#shape).
/// The handler records each outcome once from this.
public sealed record VolumeRequestEnded(
    PendingRequest<VolumeRequestKind> Request,
    IReadOnlyList<VolumeSnapshot> SnapshotsWhoseDeleteEnded);

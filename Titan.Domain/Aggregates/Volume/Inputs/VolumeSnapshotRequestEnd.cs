using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// The report that ends a volume snapshot's request (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the snapshot's provider id when the worker found it, and for a Failed the fixed reason and the raw detail.
public sealed record VolumeSnapshotRequestEnd(RequestOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static VolumeSnapshotRequestEnd Done(string? externalId) => new(RequestOutcome.Done, externalId, null, null);

    public static VolumeSnapshotRequestEnd Failed(string? externalId, string? reason, string? detail) =>
        new(RequestOutcome.Failed, externalId, reason, detail);
}

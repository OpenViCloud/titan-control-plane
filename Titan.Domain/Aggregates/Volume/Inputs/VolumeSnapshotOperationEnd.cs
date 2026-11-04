using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// The report that ends a volume snapshot's operation (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the snapshot's provider id when the worker found it, and for a Failed the fixed reason and the raw detail.
public sealed record VolumeSnapshotOperationEnd(OperationOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static VolumeSnapshotOperationEnd Done(string? externalId) => new(OperationOutcome.Done, externalId, null, null);

    public static VolumeSnapshotOperationEnd Failed(string? externalId, string? reason, string? detail) =>
        new(OperationOutcome.Failed, externalId, reason, detail);
}

using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// The report that ends a VM snapshot's request (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the snapshot's provider id when the worker found it, and for a Failed the fixed reason and the raw detail.
public sealed record VmSnapshotRequestEnd(RequestOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static VmSnapshotRequestEnd Done(string? externalId) => new(RequestOutcome.Done, externalId, null, null);

    public static VmSnapshotRequestEnd Failed(string? externalId, string? reason, string? detail) =>
        new(RequestOutcome.Failed, externalId, reason, detail);
}

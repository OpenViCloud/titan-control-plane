using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// The report that ends a VM snapshot's operation (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the snapshot's provider id when the worker found it, and for a Failed the fixed reason and the raw detail.
public sealed record VmSnapshotOperationEnd(OperationOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static VmSnapshotOperationEnd Done(string? externalId) => new(OperationOutcome.Done, externalId, null, null);

    public static VmSnapshotOperationEnd Failed(string? externalId, string? reason, string? detail) =>
        new(OperationOutcome.Failed, externalId, reason, detail);
}

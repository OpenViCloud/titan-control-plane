using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// The report that ends a VM's operation (titan-ops architecture/resource-lifecycle.md#callback-routes): how it ended, and
/// what the worker saw while doing it — the instance name when the VM had none yet, the instance's status and
/// addresses when it read them, and for a Failed the fixed reason the tenant reads and the raw detail.
public sealed record VmOperationEnd(
    OperationOutcome Outcome,
    string? ExternalId,
    VmObservation? Observed,
    string? Reason,
    string? Detail)
{
    public static VmOperationEnd Done(string? externalId, VmObservation? observed) =>
        new(OperationOutcome.Done, externalId, observed, null, null);

    public static VmOperationEnd Failed(string? externalId, VmObservation? observed, string? reason, string? detail) =>
        new(OperationOutcome.Failed, externalId, observed, reason, detail);
}

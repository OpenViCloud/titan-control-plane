using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// The report that ends a network's operation (titan-ops architecture/resource-lifecycle.md#callback-routes): how it ended,
/// and what the worker saw while doing it — the network name when the network had none yet, what it read of the
/// network, and for a Failed the fixed reason the tenant reads and the raw detail.
public sealed record NetworkOperationEnd(
    OperationOutcome Outcome,
    string? ExternalId,
    NetworkObservation? Observed,
    string? Reason,
    string? Detail)
{
    public static NetworkOperationEnd Done(string? externalId, NetworkObservation? observed) =>
        new(OperationOutcome.Done, externalId, observed, null, null);

    public static NetworkOperationEnd Failed(string? externalId, NetworkObservation? observed, string? reason, string? detail) =>
        new(OperationOutcome.Failed, externalId, observed, reason, detail);
}

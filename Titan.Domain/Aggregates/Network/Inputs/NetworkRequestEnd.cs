using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// The report that ends a network's request (titan-ops architecture/resource-lifecycle.md#callback-routes): how it ended,
/// and what the worker saw while doing it — the network name when the network had none yet, what it read of the
/// network, and for a Failed the fixed reason the tenant reads and the raw detail.
public sealed record NetworkRequestEnd(
    RequestOutcome Outcome,
    string? ExternalId,
    NetworkObservation? Observed,
    string? Reason,
    string? Detail)
{
    public static NetworkRequestEnd Done(string? externalId, NetworkObservation? observed) =>
        new(RequestOutcome.Done, externalId, observed, null, null);

    public static NetworkRequestEnd Failed(string? externalId, NetworkObservation? observed, string? reason, string? detail) =>
        new(RequestOutcome.Failed, externalId, observed, reason, detail);
}

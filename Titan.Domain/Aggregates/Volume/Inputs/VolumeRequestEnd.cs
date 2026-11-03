using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// The report that ends a volume's request (titan-ops architecture/resource-lifecycle.md#callback-routes): how it ended,
/// and what the worker saw while doing it — the volume name when the volume had none yet, what uses the volume when it
/// read that, and for a Failed the fixed reason the tenant reads and the raw detail.
public sealed record VolumeRequestEnd(
    RequestOutcome Outcome,
    string? ExternalId,
    VolumeObservation? Observed,
    string? Reason,
    string? Detail)
{
    public static VolumeRequestEnd Done(string? externalId, VolumeObservation? observed) =>
        new(RequestOutcome.Done, externalId, observed, null, null);

    public static VolumeRequestEnd Failed(string? externalId, VolumeObservation? observed, string? reason, string? detail) =>
        new(RequestOutcome.Failed, externalId, observed, reason, detail);
}

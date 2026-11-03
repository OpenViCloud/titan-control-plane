using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.LoadBalancer;

/// The report that ends a load balancer's request (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the VIP when the worker found it on Incus, and for a Failed the fixed reason and the raw detail.
public sealed record LoadBalancerRequestEnd(RequestOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static LoadBalancerRequestEnd Done(string? externalId) => new(RequestOutcome.Done, externalId, null, null);

    public static LoadBalancerRequestEnd Failed(string? externalId, string? reason, string? detail) =>
        new(RequestOutcome.Failed, externalId, reason, detail);
}

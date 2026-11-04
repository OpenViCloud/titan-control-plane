using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.LoadBalancer;

/// The report that ends a load balancer's operation (titan-ops architecture/resource-lifecycle.md#callback-routes): how it
/// ended, the VIP when the worker found it on Incus, and for a Failed the fixed reason and the raw detail.
public sealed record LoadBalancerOperationEnd(OperationOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static LoadBalancerOperationEnd Done(string? externalId) => new(OperationOutcome.Done, externalId, null, null);

    public static LoadBalancerOperationEnd Failed(string? externalId, string? reason, string? detail) =>
        new(OperationOutcome.Failed, externalId, reason, detail);
}

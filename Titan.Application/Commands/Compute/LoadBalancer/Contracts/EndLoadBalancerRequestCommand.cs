namespace Titan.Application.Commands.Compute.LoadBalancer.Contracts;

// The report of a load balancer request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Both ids come from the route: the request id is the one the command carries. Outcome is "Done" or "Failed";
// ExternalId is the VIP when the worker found it on Incus.
public record EndLoadBalancerRequestCommand(
    Guid LoadBalancerId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

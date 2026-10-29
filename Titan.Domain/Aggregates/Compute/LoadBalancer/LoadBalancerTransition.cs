namespace Titan.Domain.Aggregates.Compute.LoadBalancer;

/// The request waiting on Incus, derived, never stored (titan-ops features/load-balancer/design.md#lb-lifecycle).
public enum LoadBalancerTransition
{
    Creating,
    Updating,
    Deleting
}

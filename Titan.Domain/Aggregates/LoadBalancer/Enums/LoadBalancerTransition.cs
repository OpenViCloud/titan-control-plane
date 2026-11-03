namespace Titan.Domain.Aggregates.LoadBalancer;

/// The move the waiting request makes, for display; derived from its kind, never stored
/// (titan-ops features/load-balancer/design.md#lb-lifecycle).
public enum LoadBalancerTransition
{
    Creating,
    Updating,
    Deleting
}

namespace Titan.Domain.Aggregates.LoadBalancer;

/// The one status of a load balancer, the only one the API shows (titan-ops features/load-balancer/design.md#lb-lifecycle).
/// Incus has no status word for a load balancer, only whether it holds one: Creating, Updating and Deleting say an
/// operation is waiting, Available and NotFound say whether Incus holds it at rest, and the last two say what is not a
/// resting load balancer.
public enum LoadBalancerStatus
{
    Creating,
    // Incus holds the load balancer and nothing waits: the only status an update starts from.
    Available,
    Updating,
    Deleting,
    // The reconciler did not find the load balancer; never Deleted, which only a delete writes, never an observation.
    NotFound,
    // The create gave up before Incus held a load balancer.
    Failed,
    Deleted
}

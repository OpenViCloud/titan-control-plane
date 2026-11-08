namespace Titan.Domain.Aggregates.LoadBalancer;

public partial class LoadBalancer
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): the status at rest, or only
    // what is seen while an operation waits; never throws, ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler saw the VIP on its network. That proves it exists, not that an operation finished.
    public void MarkObserved()
    {
        if (IsDeleted || Status == LoadBalancerStatus.Failed) return;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(LoadBalancerStatus.Available);
        }
        else
        {
            Status = LoadBalancerStatus.Available;
        }
    }

    /// The reconciler did not find it. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsDeleted || Status == LoadBalancerStatus.Failed) return;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(null);
        }
        else
        {
            Status = LoadBalancerStatus.NotFound;
        }
    }
}

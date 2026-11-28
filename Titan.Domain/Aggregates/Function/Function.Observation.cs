namespace Titan.Domain.Aggregates.Function;

public partial class Function
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): the status at rest, or only
    // what is seen while an operation waits; never throws, ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).
    // Only Active and NotFound move at rest: Inactive has no running version for a Service to serve.

    /// The reconciler found the Knative Service in the tenant's namespace. That proves it exists, not that an operation
    /// finished.
    public void MarkObserved()
    {
        if (IsDeleted) return;
        if (IsWaiting)
        {
            Operation = Operation.Saw(FunctionStatus.Active);
        }
        else if (Status == FunctionStatus.NotFound)
        {
            Status = FunctionStatus.Active;
        }
    }

    /// The reconciler did not find the Service. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis). While an operation waits, a Service seen before is
    /// now missing, and one never seen stays never seen.
    public void MarkNotFound()
    {
        if (IsDeleted) return;
        if (IsWaiting)
        {
            FunctionStatus? known = null;
            if (Operation.LastSeenStatus != null)
            {
                known = FunctionStatus.NotFound;
            }
            Operation = Operation.Saw(known);
        }
        else if (Status == FunctionStatus.Active)
        {
            Status = FunctionStatus.NotFound;
        }
    }
}

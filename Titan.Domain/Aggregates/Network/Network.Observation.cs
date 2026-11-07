using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class Network
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what Incus has,
    // so no guard, and no operation ends here (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the network on Incus. Created, Errored or Unavailable after a create is the network's status, not
    /// the create's ending: only the create command's own report ends it. While an operation waits the status stays and
    /// only the status seen is recorded; Created seen before a delete finishes is legitimate.
    public void MarkObserved(NetworkStatus status)
    {
        EnsureNotForeign(status);
        if (IsDeleted || Status == NetworkStatus.Failed) return;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(status);
        }
        else
        {
            Operation = Operation.Saw(null);
            Status = status;
        }
    }

    /// The reconciler did not find the network. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsDeleted || Status == NetworkStatus.Failed) return;
        Operation = Operation.Saw(null);
        if (!IsTransitionStatus)
        {
            Status = NetworkStatus.NotFound;
        }
    }
}

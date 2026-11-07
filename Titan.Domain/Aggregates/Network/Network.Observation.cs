using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class Network
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what Incus has,
    // so no guard, and no operation ends here (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the network on Incus. Created, Errored or Unavailable after a create is the network's state,
    /// not the create's ending: only the create command's own report ends it.
    public void MarkObserved(NetworkStatus status)
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = status;
    }

    /// The reconciler did not find the network. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
    }
}

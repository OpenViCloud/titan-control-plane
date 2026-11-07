using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class FirewallRule
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, never throws,
    // ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the rule in its ACL.
    internal void MarkObserved(FirewallRuleState state)
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = state;
    }

    /// The reconciler did not find the rule in its ACL. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
    }
}

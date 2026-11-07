using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class FirewallRule
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, never throws,
    // ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the rule in its ACL. While an operation waits the status stays and only the state seen is recorded.
    internal void MarkObserved(FirewallRuleStatus status)
    {
        EnsureNotForeign(status);
        if (IsDeleted || Status == FirewallRuleStatus.Failed) return;
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

    /// The reconciler did not find the rule in its ACL. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsDeleted || Status == FirewallRuleStatus.Failed) return;
        Operation = Operation.Saw(null);
        if (!IsTransitionStatus)
        {
            Status = FirewallRuleStatus.NotFound;
        }
    }
}

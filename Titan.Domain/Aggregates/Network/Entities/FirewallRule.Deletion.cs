using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class FirewallRule
{
    /// Incus deleted the network's ACL, and this rule with it: the network's delete Done ends the rule, whatever it waited
    /// on. A rule already Deleted stays as it is.
    internal void EndWithItsNetwork()
    {
        if (IsDeleted)
        {
            return;
        }
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        Operation = Operation.ReturnedToRest();
    }

    /// Intent: remove a rule Incus may hold — whatever its state, even not found; Incus answers. With a provider id,
    /// the current pair waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refuses a
    /// repeat and a rule already deleted; the handler answers those
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    /// A rule that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    internal OperationTicket? Delete()
    {
        if (CreateIsWaiting)
        {
            throw new BusinessException("FIREWALL_RULE_NOT_READY",
                "Firewall rule is not yet provisioned. Wait for activation before deleting.");
        }

        if (DeleteIsWaiting || IsDeleted)
        {
            throw new BusinessException("FIREWALL_RULE_INVALID_STATE",
                $"A firewall rule that is {CurrentExistence} (asked: {DesiredExistence}) cannot be deleted through Incus.");
        }
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && Operation.PendingId == null;
        if (neverReachedIncus)
        {
            // A failed create may have reported a state with no id; a deleted rule shows none.
            DesiredExistence = DesiredExistence.Deleted;
            CurrentExistence = CurrentExistence.Deleted;
            CurrentState = null;
            Operation = Operation.ReturnedToRest();
            return null;
        }

        string networkProviderId = GetExternalId();
        Guid operationId = Guid.NewGuid();
        DesiredExistence = DesiredExistence.Deleted;
        Operation = Operation.Asked(operationId, FirewallRuleOperationKind.Delete, DateTimeOffset.UtcNow);
        return OperationTicket.Of(operationId, networkProviderId);
    }

    /// The delete command ended Done: the rule is gone from its ACL. Ignored, returning false, when it names another
    /// operation than the delete the rule waits on.
    internal bool DeleteSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, FirewallRuleOperationKind.Delete))
        {
            return false;
        }
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        Operation = Operation.Done();
        return true;
    }

    /// The delete command gave up: records the state the worker read and the reason, and gives the rule back. Still in the
    /// ACL and still enforced, it counts against the quota again. Ignored, returning false, when it names another
    /// operation than the delete the rule waits on.
    internal bool DeleteFailed(Guid operationId, FirewallRuleState? observedState, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, FirewallRuleOperationKind.Delete))
        {
            return false;
        }
        RecordWhatTheWorkerSaw(null, observedState);
        DesiredExistence = DesiredExistence.Exists;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        return true;
    }
}

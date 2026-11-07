using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class Network
{
    // ---- The provider id is handed out only after validating
    // (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).

    /// Intent. Follows Incus: whatever its word, even not found, a network with a provider id is deleted through
    /// Incus, which answers. With a provider id, the current pair waits for the delete command
    /// (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refuses a repeat
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). Internal: NetworkDeletion is the only way in
    /// (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    /// A network that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    internal OperationTicket? Delete()
    {
        if (DeleteIsWaiting)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "A delete is already in progress for this network.");
        }
        if (IsDeleted)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "The network is already deleted.");
        }
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && Operation.PendingId == null;
        if (neverReachedIncus)
        {
            DeleteLocally();
            return null;
        }

        if (CreateIsWaiting || ExternalId is null)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "Network is not yet provisioned.");
        }

        Guid operationId = Guid.NewGuid();
        DesiredExistence = DesiredExistence.Deleted;
        Operation = Operation.Asked(operationId, NetworkOperationKind.Delete, DateTimeOffset.UtcNow);
        return OperationTicket.Of(operationId, ExternalId);
    }

    /// A network that never reached Incus has no command and no observation coming, so Delete writes the
    /// current pair here.
    private void DeleteLocally()
    {
        // A create that gave up before Incus let go of the /64 (CreateFailed). A network still holding one would keep
        // its route once Deleted here: no delete report follows a local delete, so nothing takes the route back.
        if (HasPublicIpv6)
        {
            throw new BusinessException("NETWORK_INVALID_STATE",
                "A network still holding its public IPv6 /64 cannot be deleted locally.");
        }
        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        Operation = Operation.ReturnedToRest();
    }

    /// The delete command ended Done: Incus no longer has the network, and its ACL went with it, and every rule in it. Every
    /// rule not yet Deleted goes with the network (titan-ops features/network/design.md#network-lifecycle). Ignored,
    /// returning false, when it names another operation than the delete the network waits on.
    internal bool DeleteSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, NetworkOperationKind.Delete))
        {
            return false;
        }
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        foreach (FirewallRule rule in _firewallRules)
        {
            rule.EndWithItsNetwork();
        }
        Operation = Operation.Done();
        return true;
    }

    /// The delete command gave up: records the status the worker read and the reason, and puts desired back, so a press of
    /// Delete is a new operation. Ignored, returning false, when it names another operation than the delete the network
    /// waits on.
    internal bool DeleteFailed(Guid operationId, NetworkStatus? status, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, NetworkOperationKind.Delete))
        {
            return false;
        }
        RecordWhatTheWorkerSaw(null, status, null, null);
        DesiredExistence = DesiredExistence.Exists;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        return true;
    }
}

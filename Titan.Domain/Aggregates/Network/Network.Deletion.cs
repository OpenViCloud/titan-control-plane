using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class Network
{
    // ---- The provider id is handed out only after validating
    // (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).

    /// Intent. Follows Incus: whatever its status, even not found, a network with a provider id is deleted through
    /// Incus, which answers. With a provider id the network is Deleting and waits for the delete command
    /// (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm), remembering the Incus status it held.
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). Internal: NetworkDeletion is
    /// the only way in (titan-ops architecture/domain-services.md#cross-aggregate-lock).
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
        bool neverReachedIncus = ExternalId is null && !CreateIsWaiting;
        if (neverReachedIncus)
        {
            DeleteLocally();
            return null;
        }

        if (CreateIsWaiting)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "Network is not yet provisioned.");
        }

        Guid operationId = Guid.NewGuid();
        NetworkStatus? seen = InstanceStatus;
        Status = NetworkStatus.Deleting;
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, seen);
        return OperationTicket.Of(operationId, GetExternalId());
    }

    /// A network that never reached Incus has no command and no observation coming, so Delete writes the end status here.
    private void DeleteLocally()
    {
        // A create that gave up before Incus let go of the /64 (CreateFailed). A network still holding one would keep
        // its route once Deleted here: no delete report follows a local delete, so nothing takes the route back.
        if (HasPublicIpv6)
        {
            throw new BusinessException("NETWORK_INVALID_STATE",
                "A network still holding its public IPv6 /64 cannot be deleted locally.");
        }
        Status = NetworkStatus.Deleted;
        Operation = Operation.ReturnedToRest();
    }

    /// The delete command ended Done: Incus no longer has the network, and its ACL went with it, and every rule in it. Every
    /// rule not yet Deleted goes with the network (titan-ops features/network/design.md#network-lifecycle). Ignored,
    /// returning false, when it names another operation than the delete the network waits on.
    internal bool DeleteSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, NetworkStatus.Deleting))
        {
            return false;
        }
        Status = NetworkStatus.Deleted;
        foreach (FirewallRule rule in _firewallRules)
        {
            rule.EndWithItsNetwork();
        }
        Operation = Operation.Done();
        return true;
    }

    /// The delete command gave up: the network rests again in the Incus status the report carries, else the one it held,
    /// else Unknown, with the reason; a press of Delete is a new operation. Ignored, returning false, when it names another
    /// operation than the delete the network waits on.
    internal bool DeleteFailed(Guid operationId, NetworkStatus? status, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, NetworkStatus.Deleting))
        {
            return false;
        }
        EnsureNotForeign(status);
        NetworkStatus resting = status ?? InstanceStatus ?? NetworkStatus.Unknown;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }
}

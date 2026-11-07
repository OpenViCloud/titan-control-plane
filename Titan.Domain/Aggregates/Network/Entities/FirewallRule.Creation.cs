using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class FirewallRule
{
    /// Called by Network.AddFirewallRule after the network checked itself, with the id the handler chose.
    internal static FirewallRule Create(
        Guid id, Guid networkId, Guid tenantId,
        FirewallDirection direction, FirewallProtocol protocol,
        int? startPort, int? endPort, string cidr)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "A firewall rule needs an id.");
        }
        var rule = new FirewallRule
        {
            Id = id,
            NetworkId = networkId,
            TenantId = tenantId,
            Direction = direction,
            Protocol = protocol,
            StartPort = startPort,
            EndPort = endPort,
            Cidr = FirewallCidr.Parse(cidr),
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            CreatedAt = DateTimeOffset.UtcNow
        };
        rule.Operation = rule.Operation.Asked(Guid.NewGuid(), FirewallRuleOperationKind.Create, DateTimeOffset.UtcNow);
        return rule;
    }

    /// The create this rule waits on, for its command. The rule is not on Incus yet, so the command acts on its
    /// network there: the ticket carries the network's provider id, which the network hands in after its own check.
    internal OperationTicket TicketForCreate(string networkProviderId)
    {
        bool noCreateIsWaiting = Operation.PendingId == null || Operation.Kind != FirewallRuleOperationKind.Create;
        if (noCreateIsWaiting)
        {
            throw new BusinessException("FIREWALL_RULE_INVALID_STATE", "No create is waiting for this rule.");
        }
        return OperationTicket.Of(Operation.PendingId!.Value, networkProviderId);
    }

    /// The create command ended Done: records the id and the state the worker read, and the rule waits on nothing. A report
    /// that names another operation than the create the rule waits on is ignored and returns false.
    internal bool CreateSucceeded(Guid operationId, string? externalId, FirewallRuleState? observedState)
    {
        if (!IsWaitingOn(operationId, FirewallRuleOperationKind.Create))
        {
            return false;
        }
        EnsureTheDoneLeavesAnId(externalId);
        RecordWhatTheWorkerSaw(externalId, observedState);
        Operation = Operation.Done();
        return true;
    }

    /// The create command gave up: records the reason, and keeps an id and a state the worker found. Ignored, returning
    /// false, when it names another operation than the create the rule waits on.
    internal bool CreateFailed(Guid operationId, string? externalId, FirewallRuleState? observedState,
        string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, FirewallRuleOperationKind.Create))
        {
            return false;
        }
        RecordWhatTheWorkerSaw(externalId, observedState);
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        return true;
    }
}

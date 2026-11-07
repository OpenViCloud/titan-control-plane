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
            Status = FirewallRuleStatus.Creating,
            CreatedAt = DateTimeOffset.UtcNow
        };
        rule.Operation = rule.Operation.Asked(Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        return rule;
    }

    /// The create this rule waits on, for its command. The rule is not on Incus yet, so the command acts on its
    /// network there: the ticket carries the network's provider id, which the network hands in after its own check.
    internal OperationTicket TicketForCreate(string networkProviderId)
    {
        bool noCreateIsWaiting = Operation.PendingId == null || !CreateIsWaiting;
        if (noCreateIsWaiting)
        {
            throw new BusinessException("FIREWALL_RULE_INVALID_STATE", "No create is waiting for this rule.");
        }
        return OperationTicket.Of(Operation.PendingId!.Value, networkProviderId);
    }

    /// The create command ended Done: records the id, and the rule waits on nothing. Its status is the state the worker read,
    /// Unknown when it read none (the next observation writes the real one). A report that names another operation than the
    /// create the rule waits on is ignored and returns false.
    internal bool CreateSucceeded(Guid operationId, string? externalId, FirewallRuleStatus? status)
    {
        if (!IsWaitingOn(operationId, FirewallRuleStatus.Creating))
        {
            return false;
        }
        EnsureTheDoneLeavesAnId(externalId);
        EnsureNotForeign(status);
        RecordTheId(externalId);
        Operation = Operation.Done();
        Status = status ?? FirewallRuleStatus.Unknown;
        return true;
    }

    /// The create command gave up: records the reason. Before Incus held a rule (the report brings no id) it is Failed; after
    /// (the report names it) it rests in the state the report carries, else the one it held, else Unknown. Ignored,
    /// returning false, when it names another operation than the create the rule waits on.
    internal bool CreateFailed(Guid operationId, string? externalId, FirewallRuleStatus? status,
        string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, FirewallRuleStatus.Creating))
        {
            return false;
        }
        EnsureNotForeign(status);
        RecordTheId(externalId);
        FirewallRuleStatus resting = status ?? InstanceStatus ?? FirewallRuleStatus.Unknown;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        if (ExternalId is null)
        {
            Status = FirewallRuleStatus.Failed;
        }
        else
        {
            Status = resting;
        }
        return true;
    }
}

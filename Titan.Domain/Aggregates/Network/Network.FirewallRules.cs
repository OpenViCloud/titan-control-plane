using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class Network
{
    // ---- Firewall rules: the network counts its rules and routes each call to the rule it names
    // (titan-ops architecture/code-structure.md#child-entity). Logic stays on FirewallRule; this is the way in.

    /// Intent: a new rule in this network's ACL, with the id the handler chose. The network must be usable. The create
    /// operation is the rule's own Operation; its ticket carries the network's provider id, whose ACL the command writes to.
    public OperationTicket AddFirewallRule(
        Guid ruleId, FirewallDirection direction, FirewallProtocol protocol,
        PortRange? ports, FirewallCidr cidr)
    {
        if (!IsUsable)
            throw new BusinessException("FIREWALL_NETWORK_NOT_READY", "Firewall rules can only be added to a network Incus has created.");
        int? startPort = null;
        int? endPort = null;
        if (ports != null)
        {
            startPort = ports.Start;
            endPort = ports.End;
        }
        FirewallRule rule = FirewallRule.Create(ruleId, Id, TenantId, direction, protocol, startPort, endPort, cidr.Value);
        _firewallRules.Add(rule);
        return rule.TicketForCreate(GetExternalId());
    }

    /// A rule this network does not have answers like a missing one, the same 404 the handler gives a rule of
    /// another network (titan-ops features/network/design.md#firewall-rule-soft-delete).
    private FirewallRule GetFirewallRule(Guid ruleId)
    {
        foreach (FirewallRule rule in _firewallRules)
        {
            if (rule.Id == ruleId)
            {
                return rule;
            }
        }
        throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
    }

    /// Intent on one rule: its delete goes through Incus; returns the operation and the rule's provider id for the command,
    /// or no ticket when the rule never reached Incus and is deleted here.
    public OperationTicket? DeleteFirewallRule(Guid ruleId) => GetFirewallRule(ruleId).Delete();

    /// The reports of the rule's own commands (titan-ops features/network/design.md#network-operation); each is false when it
    /// names another operation than the one of its kind the rule waits on. A rule that is not on this network answers
    /// like a missing one.
    public bool FirewallRuleCreateSucceeded(Guid ruleId, Guid operationId, string? externalId,
        FirewallRuleState? observedState) =>
        GetFirewallRule(ruleId).CreateSucceeded(operationId, externalId, observedState);

    public bool FirewallRuleCreateFailed(Guid ruleId, Guid operationId, string? externalId,
        FirewallRuleState? observedState, string? reason, string? detail) =>
        GetFirewallRule(ruleId).CreateFailed(operationId, externalId, observedState, reason, detail);

    public bool FirewallRuleDeleteSucceeded(Guid ruleId, Guid operationId) =>
        GetFirewallRule(ruleId).DeleteSucceeded(operationId);

    public bool FirewallRuleDeleteFailed(Guid ruleId, Guid operationId, FirewallRuleState? observedState,
        string? reason, string? detail) =>
        GetFirewallRule(ruleId).DeleteFailed(operationId, observedState, reason, detail);

    // Observations of one rule (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkFirewallRuleObserved(Guid ruleId, FirewallRuleState state) =>
        GetFirewallRule(ruleId).MarkObserved(state);

    public void MarkFirewallRuleNotFound(Guid ruleId) => GetFirewallRule(ruleId).MarkNotFound();
}

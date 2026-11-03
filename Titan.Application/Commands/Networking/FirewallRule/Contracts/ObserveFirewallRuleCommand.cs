namespace Titan.Application.Commands.Networking.FirewallRule.Contracts;

// The reconciler's observation of one firewall rule (titan-ops architecture/resource-lifecycle.md#callback-routes): whether
// its network's ACL has it and, when it does, its state word for word. Both ids come from the route.
public record ObserveFirewallRuleCommand(Guid NetworkId, Guid RuleId, bool Exists, string? State = null);

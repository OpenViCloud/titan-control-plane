namespace Titan.Application.Commands.Networking.FirewallRule.Contracts;

// The worker's callback payload: an observation, but it writes, so it is a command. NetworkId and RuleId come from
// the route, the rest from the body.
//
// Report says which of four reports this is (titan-ops features/network/design.md#firewall-rule-status): Observed (the
// reconciler: Exists, and State — the Incus ACL rule state word for word — when it exists), Created and Deleted (the
// create and delete commands), and a command's give-up (Reason/Detail; the create's may carry the id).
public record UpdateFirewallRuleStatusCommand(
    Guid NetworkId,
    Guid RuleId,
    string Report,
    bool? Exists = null,
    string? State = null,
    string? ExternalRuleId = null,
    string? Reason = null,
    string? Detail = null);

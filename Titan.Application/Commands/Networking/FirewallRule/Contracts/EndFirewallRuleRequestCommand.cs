namespace Titan.Application.Commands.Networking.FirewallRule.Contracts;

// The report of a firewall rule request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes). The
// three ids come from the route. Outcome is "Done" or "Failed"; State is the ACL rule state the worker read, absent when
// it did not read it.
public record EndFirewallRuleRequestCommand(
    Guid NetworkId,
    Guid RuleId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    string? State = null,
    string? Reason = null,
    string? Detail = null);

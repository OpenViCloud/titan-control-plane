namespace Titan.Application.Queries.Networking.FirewallRule.Contracts;

// The firewall reconciler asks for the rules of one cluster's networks it must observe.
public record ListReconcilableFirewallRulesQuery(string ClusterId);

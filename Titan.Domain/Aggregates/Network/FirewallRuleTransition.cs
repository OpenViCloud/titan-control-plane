namespace Titan.Domain.Aggregates.Network;

/// The request still waiting for the provider, derived — never stored.
public enum FirewallRuleTransition
{
    Creating,
    Deleting
}

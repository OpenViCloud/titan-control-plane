namespace Titan.Domain.Aggregates.Network;

/// The operation still waiting for the provider, derived — never stored.
public enum FirewallRuleTransition
{
    Creating,
    Deleting
}

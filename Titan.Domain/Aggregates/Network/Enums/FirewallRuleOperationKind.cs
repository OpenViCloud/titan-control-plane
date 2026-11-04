namespace Titan.Domain.Aggregates.Network;

/// The kinds of operation a firewall rule can wait on, one at a time and independent of its network's
/// (titan-ops architecture/resource-lifecycle.md#kinds).
public enum FirewallRuleOperationKind
{
    Create,
    Delete
}

namespace Titan.Domain.Aggregates.Network;

/// The one status of a firewall rule, the only one the API shows (titan-ops features/network/design.md#firewall-rule-status).
/// Two say an operation is waiting; three are the state of the Incus ACL rule, word for word and lower case as Incus
/// spells them (FirewallRule.IsIncusStatus, the only ones FirewallRule.InstanceStatus can be); the last four say what is not
/// a resting rule.
public enum FirewallRuleStatus
{
    Creating,
    Deleting,
    enabled,
    disabled,
    logged,
    // The operation ended without the worker reading the rule; the next observation writes the real state.
    Unknown,
    // The reconciler did not find the rule in its network's ACL; never Deleted, which only the delete command's Done writes.
    NotFound,
    // The create gave up before Incus held a rule.
    Failed,
    Deleted
}

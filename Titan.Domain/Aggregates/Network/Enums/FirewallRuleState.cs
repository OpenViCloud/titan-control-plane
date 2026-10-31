namespace Titan.Domain.Aggregates.Network;

/// The state of an Incus ACL rule, word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim):
/// the three Incus 7.0.1 accepts, lower case as Incus spells them — so the name of each member is the word stored and
/// sent, with no mapping on either side. Only an observation writes it; FirewallRule.CurrentState is null while the
/// rule is not in its network's ACL.
public enum FirewallRuleState
{
    enabled,
    disabled,
    logged
}

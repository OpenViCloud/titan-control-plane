using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// One row of the firewall reconciler's list (titan-ops features/network/user-stories.md#us-net-intent-04): the columns themselves, not an aggregate, so
/// a stored CIDR that breaks its rule does not cost the worker this list for the cluster.
public record ReconcilableFirewallRule(
    Guid Id, Guid NetworkId, string ExternalId, FirewallRuleState? CurrentState, CurrentExistence CurrentExistence);

namespace Titan.Domain.Aggregates.Network;

/// The new rule and the provider id of its network, handed out together past the network's own check
/// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public sealed record FirewallRuleTicket(FirewallRule Rule, string ExternalNetworkId);

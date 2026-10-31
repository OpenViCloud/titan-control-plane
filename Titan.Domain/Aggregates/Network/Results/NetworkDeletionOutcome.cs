namespace Titan.Domain.Aggregates.Network;

/// What Network.MarkDeleted did, in one answer: whether the report fulfilled the network's own waiting delete, and
/// which of its rules had a delete of their own waiting that the network's delete finished for them. One call tells
/// the callback both, so it cannot record the network's outcome and forget the rules'.
public record NetworkDeletionOutcome(bool FulfilledNetworkDelete, IReadOnlyList<FirewallRule> RulesWhoseDeleteWasFulfilled);

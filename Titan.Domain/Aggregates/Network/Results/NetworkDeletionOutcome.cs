namespace Titan.Domain.Aggregates.Network;

/// What Network.MarkDeleted did, in one answer: whether the report fulfilled the network's own waiting delete, which
/// of its rules had a delete of their own waiting that the network's delete finished for them, and whether the network
/// let go of a /64 block the caller takes back in the cluster's pool. One call tells the callback all three, so it
/// cannot record the network's outcome and forget the rules' or the block.
public record NetworkDeletionOutcome(
    bool FulfilledNetworkDelete, IReadOnlyList<FirewallRule> RulesWhoseDeleteWasFulfilled, bool LetGoOfBlock64);

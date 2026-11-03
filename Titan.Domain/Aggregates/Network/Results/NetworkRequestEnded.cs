using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// What Network.EndRequest ended, in one answer (titan-ops architecture/resource-lifecycle.md#shape): the request the
/// report named; when a delete ended, the rules whose own delete was waiting and went with the network's ACL; and whether
/// a create that failed before Incus had the network let go of its public IPv6 /64, so NetworkRoutes gives the route back.
/// The handler records each outcome once from this.
public sealed record NetworkRequestEnded(
    PendingRequest<NetworkRequestKind> Request,
    IReadOnlyList<FirewallRule> RulesWhoseDeleteEnded,
    bool LetGoOfPublicIpv6);

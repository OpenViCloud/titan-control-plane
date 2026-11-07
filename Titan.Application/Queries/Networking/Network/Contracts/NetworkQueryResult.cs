using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Queries.Networking.Network.Contracts;

// Status is the one status of the network (titan-ops features/network/design.md#network-lifecycle): the Incus network status
// word for word while it rests, or what is not a resting network (Creating, Deleting, NotFound, Failed, Deleted). A command's
// give-up is the request failure; its raw detail is admin-only (titan-ops architecture/desired-and-current.md#request-failure).
public record NetworkQueryResult(
    Guid Id,
    string Name,
    string ClusterId,
    string? Cidr,
    string? Gateway,
    string? ExternalId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] NetworkStatus Status,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt,
    string DefaultIngressPolicy,
    string DefaultEgressPolicy,
    DateTimeOffset CreatedAt,
    // The network's public /64, null = no public IPv6 (titan-ops infra/incus/design.md#ipv6-address-plan).
    string? PublicIpv6Subnet)
{
    public static NetworkQueryResult From(NetworkAggregate n) =>
        new(n.Id, n.Name.Value, n.ClusterId, n.Cidr, n.Gateway, n.ExternalId, n.Status,
            n.Operation.FailureReason, n.Operation.FailedAt,
            n.DefaultIngressPolicy.ToString(), n.DefaultEgressPolicy.ToString(), n.CreatedAt, n.PublicIpv6Subnet);
}

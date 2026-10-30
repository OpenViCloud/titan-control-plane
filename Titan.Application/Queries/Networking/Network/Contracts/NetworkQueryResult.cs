using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Networking.Network.Contracts;

// CurrentState is the Incus network status word for word, null while Incus has no network; CurrentExistence says
// whether Incus holds it, DesiredExistence what the customer asked for, Transition the request still waiting
// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim). A command's give-up is the request
// failure; its raw detail is admin-only (titan-ops architecture/desired-and-current.md#request-failure).
public record NetworkQueryResult(
    Guid Id,
    string Name,
    string ClusterId,
    string? Cidr,
    string? Gateway,
    string? ExternalId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] NetworkStatus? CurrentState,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CurrentExistence CurrentExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DesiredExistence DesiredExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] NetworkTransition? Transition,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt,
    string DefaultIngressPolicy,
    string DefaultEgressPolicy,
    DateTimeOffset CreatedAt,
    // The network's public /64, null = no public IPv6 (titan-ops infra/incus/design.md#ipv6-address-plan).
    string? PublicIpv6Subnet)
{
    public static NetworkQueryResult From(NetworkAggregate n) =>
        new(n.Id, n.Name, n.ClusterId, n.Cidr, n.Gateway, n.ExternalId, n.CurrentState,
            n.CurrentExistence, n.DesiredExistence, n.Transition,
            n.RequestFailureReason, n.RequestFailedAt,
            n.DefaultIngressPolicy.ToString(), n.DefaultEgressPolicy.ToString(), n.CreatedAt, n.PublicIpv6Subnet);
}

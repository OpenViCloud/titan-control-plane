using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Network;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Networking.FirewallRule.Contracts;

// CurrentState is the Incus ACL rule state word for word (enabled · disabled · logged), null while the rule is not
// in the ACL; the existence pair and the request failure tell a rule being created from one whose create failed
// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
public record FirewallRuleQueryResult(
    Guid Id,
    string Direction,
    string Protocol,
    int? StartPort,
    int? EndPort,
    string Cidr,
    string? ExternalId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FirewallRuleState? CurrentState,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CurrentExistence CurrentExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DesiredExistence DesiredExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FirewallRuleTransition? Transition,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt,
    DateTimeOffset CreatedAt)
{
    public static FirewallRuleQueryResult From(FirewallRuleEntity r) =>
        new(r.Id, r.Direction.ToString(), r.Protocol.ToString(),
            r.StartPort, r.EndPort, r.Cidr.Value, r.ExternalId,
            r.CurrentState, r.CurrentExistence, r.DesiredExistence,
            r.Transition, r.RequestFailureReason, r.RequestFailedAt, r.CreatedAt);
}

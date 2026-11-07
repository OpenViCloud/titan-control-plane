using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Network;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Networking.FirewallRule.Contracts;

// Status is the one status of the rule (titan-ops features/network/design.md#firewall-rule-status): the Incus ACL rule state word
// for word (enabled · disabled · logged) while the rule rests, or what is not a resting rule (Creating, Deleting, Unknown,
// NotFound, Failed, Deleted). A command's give-up is the request failure, which tells a rule whose create failed from the
// others (titan-ops architecture/desired-and-current.md#request-failure).
public record FirewallRuleQueryResult(
    Guid Id,
    string Direction,
    string Protocol,
    int? StartPort,
    int? EndPort,
    string Cidr,
    string? ExternalId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FirewallRuleStatus Status,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt,
    DateTimeOffset CreatedAt)
{
    public static FirewallRuleQueryResult From(FirewallRuleEntity r) =>
        new(r.Id, r.Direction.ToString(), r.Protocol.ToString(),
            r.StartPort, r.EndPort, r.Cidr.Value, r.ExternalId,
            r.Status, r.Operation.FailureReason, r.Operation.FailedAt, r.CreatedAt);
}

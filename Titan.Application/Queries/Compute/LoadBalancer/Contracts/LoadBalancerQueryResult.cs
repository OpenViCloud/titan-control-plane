using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.LoadBalancer;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Compute.LoadBalancer.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse). Listeners and backends are what
// the customer last asked for; transition says whether Incus has it yet. Incus has no status word for a load
// balancer (titan-ops features/load-balancer/design.md#lb-lifecycle). The failure detail is the operator's, never the
// tenant's.
public record LoadBalancerQueryResult(
    Guid Id,
    string Name,
    Guid NetworkId,
    string ClusterId,
    string Vip,
    List<LoadBalancerListenerQueryResult> Listeners,
    List<LoadBalancerBackendQueryResult> Backends,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CurrentExistence CurrentExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DesiredExistence DesiredExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] LoadBalancerTransition? Transition,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt,
    DateTimeOffset CreatedAt)
{
    public static LoadBalancerQueryResult From(LoadBalancerAggregate lb) =>
        new(lb.Id, lb.Name, lb.NetworkId, lb.ClusterId, lb.Vip,
            lb.DesiredListeners
                .OrderBy(l => l.Protocol).ThenBy(l => l.Port)
                .Select(l => new LoadBalancerListenerQueryResult(l.Protocol.ToString(), l.Port, l.TargetPort))
                .ToList(),
            lb.DesiredBackends.Select(b => new LoadBalancerBackendQueryResult(b.VmId)).ToList(),
            lb.CurrentExistence, lb.DesiredExistence, lb.Transition,
            lb.RequestFailureReason, lb.RequestFailedAt, lb.CreatedAt);
}

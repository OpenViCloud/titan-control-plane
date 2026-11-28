using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Function;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Application.Queries.Compute.Function.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse). Url is null until the first
// deploy names the Knative Service. The failure detail is the operator's, never the tenant's, as a load balancer's.
public record FunctionQueryResult(
    Guid Id,
    string Name,
    string ClusterId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] FunctionStatus Status,
    string? Url,
    Guid? RunningVersionId,
    Guid? DeployingVersionId,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt,
    DateTimeOffset CreatedAt)
{
    public static FunctionQueryResult From(FunctionAggregate function, string? url) =>
        new(function.Id, function.Name, function.ClusterId, function.Status, url, function.RunningVersionId,
            function.DeployingVersionId, function.Operation.FailureReason, function.Operation.FailedAt, function.CreatedAt);
}

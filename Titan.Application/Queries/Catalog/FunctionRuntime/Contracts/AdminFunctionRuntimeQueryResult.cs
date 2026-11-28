using FunctionRuntimeAggregate = Titan.Domain.Aggregates.FunctionRuntime.FunctionRuntime;

namespace Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;

// Admin-facing, out of the public spec like every /v1/admin route.
public record AdminFunctionRuntimeQueryResult(
    Guid Id,
    string Name,
    string Image,
    string Architecture,
    bool Listed,
    DateOnly? SupportEndsOn, // null: the language names no end (go)
    List<AdminFunctionRuntimeClusterQueryResult> Clusters) // ordered by cluster id
{
    public static AdminFunctionRuntimeQueryResult From(FunctionRuntimeAggregate r) =>
        new(r.Id, r.Name, r.Image, r.Architecture, r.IsListed, r.SupportEndsOn,
            r.Clusters.OrderBy(c => c.ClusterId, StringComparer.Ordinal).Select(AdminFunctionRuntimeClusterQueryResult.From).ToList());
}

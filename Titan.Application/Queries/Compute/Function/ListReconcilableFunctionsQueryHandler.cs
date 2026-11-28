using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.Function;

// Read side of the function reconciler of one function cluster. currentExistence is the worker's own word: Exists when
// Titan last knew the Knative Service to be there (Function.IsSeenOnTheCluster), NotFound otherwise. No ICurrentUser:
// the reconciler acts for no tenant; the endpoint authenticates it by X-Internal-Token.
public class ListReconcilableFunctionsQueryHandler(IFunctionRepository repo)
{
    public async Task<List<ReconcilableFunctionQueryResult>> HandleAsync(ListReconcilableFunctionsQuery query, CancellationToken ct = default)
    {
        var rows = await repo.ListReconcilableInClusterAsync(query.ClusterId, ct);
        return rows.Select(f => new ReconcilableFunctionQueryResult(f.Id, f.TenantId, f.IsSeenOnTheCluster ? "Exists" : "NotFound"))
            .ToList();
    }
}

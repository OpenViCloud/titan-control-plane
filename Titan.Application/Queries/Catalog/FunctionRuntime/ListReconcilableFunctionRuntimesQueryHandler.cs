using Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Catalog.FunctionRuntime;

// Read side of the runtime reconciler of one function cluster (titan-ops architecture/desired-and-current.md#desired-and-current).
public class ListReconcilableFunctionRuntimesQueryHandler(IFunctionRuntimeRepository runtimes)
{
    public async Task<List<ReconcilableFunctionRuntimeQueryResult>> HandleAsync(ListReconcilableFunctionRuntimesQuery query, CancellationToken ct = default)
    {
        var rows = await runtimes.ListInClusterAsync(query.ClusterId, ct);
        return rows.Select(r => ReconcilableFunctionRuntimeQueryResult.From(r, query.ClusterId)).ToList();
    }
}

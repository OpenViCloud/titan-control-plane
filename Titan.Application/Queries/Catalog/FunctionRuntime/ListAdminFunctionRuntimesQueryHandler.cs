using Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Catalog.FunctionRuntime;

// Every runtime with its sync state, so the operator sees which function cluster is still Syncing or Failed after a sync.
// The repository orders by name.
public class ListAdminFunctionRuntimesQueryHandler(IFunctionRuntimeRepository repo)
{
    public async Task<List<AdminFunctionRuntimeQueryResult>> HandleAsync(ListAdminFunctionRuntimesQuery query, CancellationToken ct = default)
    {
        var runtimes = await repo.ListAllAsync(ct);
        return runtimes.Select(AdminFunctionRuntimeQueryResult.From).ToList();
    }
}

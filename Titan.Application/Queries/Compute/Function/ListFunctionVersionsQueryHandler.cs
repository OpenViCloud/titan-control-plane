using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.Function;

// Kept versions only, newest first: a discarded one cannot be deployed, so it is not offered
// (titan-ops features/ovc-function/design.md#function-versions).
public class ListFunctionVersionsQueryHandler(IFunctionRepository repo, ICurrentUser currentUser)
{
    public async Task<List<FunctionVersionQueryResult>> HandleAsync(ListFunctionVersionsQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        var function = await ComputeOwnership.RequireFunctionAsync(repo, tenantId, query.FunctionId, ct);
        return function.Versions
            .Where(v => !v.IsDiscarded)
            .OrderByDescending(v => v.Number)
            .Select(FunctionVersionQueryResult.From)
            .ToList();
    }
}

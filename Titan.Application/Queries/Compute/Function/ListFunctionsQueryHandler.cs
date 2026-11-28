using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.Function;

public class ListFunctionsQueryHandler(IFunctionRepository repo, ICurrentUser currentUser, FunctionUrlOptions urls)
{
    public async Task<List<FunctionQueryResult>> HandleAsync(ListFunctionsQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        var functions = await repo.ListByTenantAsync(tenantId, ct);
        return functions.Select(f => FunctionQueryResult.From(f, urls.UrlOf(f.ExternalId))).ToList();
    }
}

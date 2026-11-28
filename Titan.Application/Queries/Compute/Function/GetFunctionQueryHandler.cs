using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Queries.Compute.Function.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.Function;

/// A Deleted function is still answered, like a Deleted load balancer.
public class GetFunctionQueryHandler(IFunctionRepository repo, ICurrentUser currentUser, FunctionUrlOptions urls)
{
    public async Task<FunctionQueryResult> HandleAsync(GetFunctionQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        var function = await ComputeOwnership.RequireFunctionAsync(repo, tenantId, query.FunctionId, ct);
        return FunctionQueryResult.From(function, urls.UrlOf(function.ExternalId));
    }
}

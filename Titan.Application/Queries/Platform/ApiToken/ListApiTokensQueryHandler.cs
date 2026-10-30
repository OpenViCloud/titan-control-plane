using Titan.Application.Abstractions;
using Titan.Application.Queries.Platform.ApiToken.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Platform.ApiToken;

/// Expired tokens are listed too: they still count toward the limit until the customer deletes them.
public class ListApiTokensQueryHandler(IApiTokenRepository repo, ICurrentUser currentUser)
{
    public async Task<List<ApiTokenQueryResult>> HandleAsync(ListApiTokensQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        var tokens = await repo.ListByTenantAsync(tenantId, ct);
        return tokens
            .Select(t => new ApiTokenQueryResult(t.Id, t.Name, t.Prefix, t.CreatedAt, t.ExpiresAt))
            .ToList();
    }
}

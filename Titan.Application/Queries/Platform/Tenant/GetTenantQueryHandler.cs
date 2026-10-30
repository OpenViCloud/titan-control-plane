using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Tenant;
using Titan.Application.Queries.Platform.Tenant.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Platform.Tenant;

/// The caller's own tenant — what the portal reads right after sign-in (TenantContext).
public class GetTenantQueryHandler(ITenantRepository repo, ICurrentUser currentUser)
{
    public async Task<TenantQueryResult> HandleAsync(GetTenantQuery query, CancellationToken ct = default)
    {
        var tenant = await CurrentTenant.GetAsync(repo, currentUser, ct);
        return TenantQueryResult.From(tenant);
    }
}

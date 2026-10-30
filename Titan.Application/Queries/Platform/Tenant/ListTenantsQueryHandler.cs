using Titan.Application.Queries.Platform.Tenant.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Platform.Tenant;

/// Platform-admin: every tenant, across the platform.
public class ListTenantsQueryHandler(ITenantRepository repo)
{
    public async Task<List<AdminTenantQueryResult>> HandleAsync(ListTenantsQuery query, CancellationToken ct = default)
    {
        var tenants = await repo.ListAllAsync(ct);
        return tenants.Select(t => AdminTenantQueryResult.From(t)).ToList();
    }
}

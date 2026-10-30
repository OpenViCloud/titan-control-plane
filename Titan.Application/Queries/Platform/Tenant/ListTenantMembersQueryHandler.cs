using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Tenant;
using Titan.Application.Queries.Platform.Tenant.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Platform.Tenant;

public class ListTenantMembersQueryHandler(ITenantRepository repo, ICurrentUser currentUser)
{
    public async Task<List<TenantMemberQueryResult>> HandleAsync(ListTenantMembersQuery query, CancellationToken ct = default)
    {
        var tenant = await CurrentTenant.GetAsync(repo, currentUser, ct);
        return tenant.Members.Select(TenantMemberQueryResult.From).ToList();
    }
}

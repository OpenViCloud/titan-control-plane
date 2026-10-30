using Titan.Application.Queries.Admin.Network.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Admin.Network;

// Cross-tenant network read (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read) for platform-admin.
public class ListAdminNetworksQueryHandler(INetworkRepository networks)
{
    public async Task<List<AdminNetworkQueryResult>> HandleAsync(ListAdminNetworksQuery query, CancellationToken ct = default) =>
        (await networks.ListAllAsync(query.TenantId, ct)).Select(AdminNetworkQueryResult.From).ToList();
}

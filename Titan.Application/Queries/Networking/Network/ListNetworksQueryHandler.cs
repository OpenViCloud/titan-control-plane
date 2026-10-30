using Titan.Application.Abstractions;
using Titan.Application.Queries.Networking.Network.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Networking.Network;

public class ListNetworksQueryHandler(INetworkRepository repo, ICurrentUser currentUser)
{
    public async Task<List<NetworkQueryResult>> HandleAsync(ListNetworksQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var networks = await repo.ListByTenantAsync(tenantId, ct);
        return networks.Select(NetworkQueryResult.From).ToList();
    }
}

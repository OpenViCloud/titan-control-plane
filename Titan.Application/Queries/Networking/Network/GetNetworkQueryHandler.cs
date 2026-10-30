using Titan.Application.Abstractions;
using Titan.Application.Services.Networking.Network;
using Titan.Application.Queries.Networking.Network.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Networking.Network;

public class GetNetworkQueryHandler(INetworkRepository repo, ICurrentUser currentUser)
{
    public async Task<NetworkQueryResult> HandleAsync(GetNetworkQuery query, CancellationToken ct = default)
    {
        var network = await NetworkOwnership.RequireNetworkAsync(repo, currentUser.RequireTenantId(), query.NetworkId, ct);
        return NetworkQueryResult.From(network);
    }
}

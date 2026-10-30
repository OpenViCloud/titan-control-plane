using Titan.Application.Queries.Networking.Network.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Networking.Network;

// Read side of the network reconciler (titan-ops architecture/desired-and-current.md#desired-and-current). No
// ICurrentUser: the reconciler acts for no tenant; the endpoint authenticates it by X-Internal-Token.
public class ListReconcilableNetworksQueryHandler(INetworkRepository repo)
{
    public async Task<List<ReconcilableNetworkQueryResult>> HandleAsync(ListReconcilableNetworksQuery query, CancellationToken ct = default)
    {
        var networks = await repo.ListReconcilableAsync(query.ClusterId, ct);
        return networks.Select(ReconcilableNetworkQueryResult.From).ToList();
    }
}

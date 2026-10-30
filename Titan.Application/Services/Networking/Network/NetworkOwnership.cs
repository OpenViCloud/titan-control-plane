using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Services.Networking.Network;

/// <summary>
/// The one place Networking decides whether a caller may see a network.
///
/// A foreign network answers <b>404</b>, exactly like a missing one — never 403, which would confirm
/// to a caller that a resource they cannot see exists (titan-ops architecture/http-contract.md#tenant-scoping).
/// NetworkServiceTests.Get_NetworkBelongsToOtherTenant_ThrowsNotFoundException pins it.
/// </summary>
public static class NetworkOwnership
{
    public static async Task<NetworkAggregate> RequireNetworkAsync(
        INetworkRepository repo, Guid tenantId, Guid networkId, CancellationToken ct)
    {
        var network = await repo.FindByIdAsync(networkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        if (network.TenantId != tenantId)
            throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        return network;
    }

    /// The same answer with the network's row locked (INetworkRepository.FindByIdForUpdateAsync): for a request that
    /// adds to the network and must count what it already holds.
    public static async Task<NetworkAggregate> RequireNetworkForUpdateAsync(
        INetworkRepository repo, Guid tenantId, Guid networkId, CancellationToken ct)
    {
        var network = await repo.FindByIdForUpdateAsync(networkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        if (network.TenantId != tenantId)
            throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        return network;
    }
}

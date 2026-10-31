using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Application.Commands.Networking.Network;

// Rename in-place (CP-only metadata, no provider command). Returns nothing (titan-ops architecture/http-contract.md#commands-return-no-data).
public class RenameNetworkCommandHandler(INetworkRepository repo, ICurrentUser currentUser)
{
    public async Task HandleAsync(RenameNetworkCommand command, CancellationToken ct = default)
    {
        var (networkId, requestedName) = command;
        NetworkName name = NetworkName.Parse(requestedName);

        var network = await NetworkOwnership.RequireNetworkAsync(repo, currentUser.RequireTenantId(), networkId, ct);

        // No-op rename: don't self-conflict against the network's own name, don't spend a write.
        bool sameName = network.Name == name;
        if (!sameName)
        {
            if (await repo.ExistsByNameAsync(network.TenantId, name, ct))
                throw new ConflictException("NETWORK_NAME_TAKEN", $"Network name '{requestedName}' is already taken.");

            network.Rename(name);
            await repo.SaveAsync(ct);
        }
    }
}

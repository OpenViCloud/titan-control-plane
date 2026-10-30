using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Networking.Network;

// Rename in-place (CP-only metadata, no provider command). Returns nothing (titan-ops architecture/http-contract.md#commands-return-no-data).
public class RenameNetworkCommandHandler(INetworkRepository repo, ICurrentUser currentUser)
{
    public async Task HandleAsync(RenameNetworkCommand command, CancellationToken ct = default)
    {
        var (networkId, name) = command;

        var network = await NetworkOwnership.RequireNetworkAsync(repo, currentUser.RequireTenantId(), networkId, ct);

        // No-op rename: don't self-conflict against the network's own name, don't spend a write.
        if (!string.Equals(network.Name, name, StringComparison.Ordinal))
        {
            if (await repo.ExistsByNameAsync(network.TenantId, name, ct))
                throw new ConflictException("NETWORK_NAME_TAKEN", $"Network name '{name}' is already taken.");

            network.Rename(name);
            await repo.SaveAsync(ct);
        }
    }
}

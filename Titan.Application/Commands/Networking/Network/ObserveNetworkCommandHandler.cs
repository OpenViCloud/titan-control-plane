using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Networking.Network;

// The reconciler's observation of one network (titan-ops architecture/resource-lifecycle.md#callback-routes): what Incus
// has; no request ends here (titan-ops architecture/resource-lifecycle.md#invariants). Authenticated
// by X-Internal-Token at the endpoint, acting for no tenant. Locked like every lifecycle handler, so an observation and a
// request on the same network take turns.
public class ObserveNetworkCommandHandler(INetworkRepository repo)
{
    public async Task HandleAsync(ObserveNetworkCommand command, CancellationToken ct = default)
    {
        // Parsed before the network is loaded: a malformed report is the worker's bug, whatever the network.
        Domain.Aggregates.Network.NetworkStatus? status = null;
        if (command.Exists)
        {
            status = NetworkReports.ParseStatus(command.Status);
        }

        Domain.Aggregates.Network.Network network = await repo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        if (status != null)
        {
            network.MarkObserved(status.Value);
        }
        else
        {
            network.MarkNotFound();
        }
        await repo.SaveAsync(ct);
    }
}

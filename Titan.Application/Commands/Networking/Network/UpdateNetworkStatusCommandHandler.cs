using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

namespace Titan.Application.Commands.Networking.Network;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): a
// worker that predates the request routes reports the reconciler's observation and the commands' results here, none of
// them naming a request. Each report is mapped onto the request the network is waiting on, so the network ends up as
// the new routes would leave it. No ICurrentUser on purpose: authenticated by X-Internal-Token at the endpoint, acting
// for no tenant. Locked like every lifecycle handler.
public class UpdateNetworkStatusCommandHandler(
    INetworkRepository repo,
    NetworkRoutes routes,
    IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateNetworkStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)

        // Parsed before the network is loaded: a malformed report is the worker's bug, whatever the network.
        NetworkStatus? observedStatus = null;
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                if (req.Exists == true)
                {
                    observedStatus = NetworkReports.ParseStatus(req.Status);
                }
                break;
            case "Created":
                if (string.IsNullOrEmpty(req.ExternalId))
                {
                    throw new BadRequestException("INVALID_STATUS", "ExternalId is required when report is Created.");
                }
                break;
            case "RequestFailed":
            case "Deleted":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown network report: '{req.Report}'");
        }

        Domain.Aggregates.Network.Network network = await repo.FindByIdForUpdateAsync(req.NetworkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        PendingOperation<NetworkOperationKind>? waiting = network.Pending;
        NetworkObservation addresses = new(null, req.Cidr, req.Gateway);
        switch (req.Report)
        {
            case "Observed":
                if (observedStatus == null)
                {
                    network.MarkNotFound();
                }
                else
                {
                    network.MarkObserved(observedStatus.Value);
                }
                break;
            case "Created":
                // The old create command reports the network name and address as soon as Incus holds it: the create's
                // Done. The status is left to the reconciler, as before.
                if (waiting is { Kind: NetworkOperationKind.Create })
                {
                    await NetworkReports.FinishOperationAsync(routes, network, waiting.Id, req.ExternalId, addresses,
                        activity, ct);
                }
                break;
            case "RequestFailed":
                // The waiting request ends Failed, keeping an id and address the create's give-up found.
                if (waiting != null)
                {
                    await NetworkReports.FailOperationAsync(routes, network, waiting.Id, req.ExternalId, addresses,
                        req.Reason, req.Detail, activity, ct);
                }
                break;
            case "Deleted":
                // Only a delete's own report ends a network; with no delete waiting the network is gone from Incus,
                // which is what NotFound records.
                if (waiting is { Kind: NetworkOperationKind.Delete })
                {
                    await NetworkReports.FinishOperationAsync(routes, network, waiting.Id, null, null, activity, ct);
                }
                else
                {
                    network.MarkNotFound();
                }
                break;
        }

        // A route given back above commits with the network that let go of it.
        await repo.SaveAsync(ct);
    }
}

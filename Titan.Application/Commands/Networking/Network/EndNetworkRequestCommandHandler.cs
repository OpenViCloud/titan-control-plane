using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Application.Services;

namespace Titan.Application.Commands.Networking.Network;

// The worker's report that a network request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant: there must never be a route to this handler
// from a customer path. The row is locked so a request made meanwhile is ordered before or after this report, never
// interleaved (titan-ops architecture/resource-lifecycle.md#one-key-per-request). A report the network is not waiting on
// is ignored and still answers 204: the worker has nothing to retry.
public class EndNetworkRequestCommandHandler(
    INetworkRepository repo,
    NetworkRoutes routes,
    IActivityRecorder activity)
{
    public async Task HandleAsync(EndNetworkRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the network is loaded: a malformed report is the worker's bug, whatever the network.
        OperationOutcome outcome = NetworkReports.ParseOutcome(command.Outcome);
        NetworkStatus? status = null;
        if (command.Status != null)
        {
            status = NetworkReports.ParseStatus(command.Status);
        }

        Domain.Aggregates.Network.Network network = await repo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        NetworkOperationKind? ended;
        if (outcome == OperationOutcome.Done)
        {
            ended = await NetworkReports.FinishOperationAsync(routes, network, command.RequestId, command.ExternalId,
                status: status, cidr: command.Cidr, gateway: command.Gateway, activity, ct);
        }
        else
        {
            ended = await NetworkReports.FailOperationAsync(routes, network, command.RequestId, command.ExternalId,
                status: status, cidr: command.Cidr, gateway: command.Gateway, reason: command.Reason, detail: command.Detail,
                activity, ct);
        }
        if (ended == null)
        {
            return;
        }

        await repo.SaveAsync(ct);
    }
}

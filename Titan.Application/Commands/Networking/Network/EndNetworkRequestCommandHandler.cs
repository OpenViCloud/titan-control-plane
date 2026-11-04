using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;

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
        RequestOutcome outcome = NetworkReports.ParseOutcome(command.Outcome);
        NetworkStatus? status = null;
        if (command.Status != null)
        {
            status = NetworkReports.ParseStatus(command.Status);
        }

        Domain.Aggregates.Network.Network network = await repo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        bool sawTheNetwork = status != null || command.Cidr != null || command.Gateway != null;
        NetworkObservation? observed = sawTheNetwork ? new NetworkObservation(status, command.Cidr, command.Gateway) : null;
        NetworkRequestEnd end = new(outcome, command.ExternalId, observed, command.Reason, command.Detail);
        // NetworkRoutes ends it, so a route the network let go of goes back in the same save.
        NetworkRequestEnded? ended = await routes.RecordEndedAsync(network, command.RequestId, end, ct);
        if (ended == null)
        {
            return;
        }

        NetworkReports.RecordEnded(network, ended, outcome, activity);
        await repo.SaveAsync(ct);
    }
}

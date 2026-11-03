using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a network request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes):
// the route names the network and the request, the body says how it ended and what the worker saw. 204 also when the
// network is not waiting on that request: the worker has nothing to retry.
public class EndNetworkRequestEndpoint(EndNetworkRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndNetworkRequestBody>
{
    public override void Configure()
    {
        Patch("internal/networks/{networkId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndNetworkRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid networkId = Route<Guid>("networkId");
        Guid requestId = Route<Guid>("requestId");
        await handler.HandleAsync(new EndNetworkRequestCommand(
            networkId, requestId, body.Outcome, body.ExternalId, body.Observed?.Status, body.Observed?.Cidr,
            body.Observed?.Gateway, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; EndNetworkRequestCommand additionally carries the two route ids.
public record EndNetworkRequestBody(
    string Outcome,
    string? ExternalId = null,
    NetworkObservedBody? Observed = null,
    string? Reason = null,
    string? Detail = null);

// What the worker read of the network: the Incus status word for word, and the IPv4 range and gateway. Shared by the
// request report and the reconciler's observation, which sends the status only.
public record NetworkObservedBody(string? Status = null, string? Cidr = null, string? Gateway = null);

using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler's observation of one network (titan-ops architecture/resource-lifecycle.md#callback-routes): whether Incus
// has it and its status. Ends no request.
public class ObserveNetworkEndpoint(ObserveNetworkCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveNetworkBody>
{
    public override void Configure()
    {
        Patch("internal/networks/{networkId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveNetworkBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A body without exists would read as "gone" and record the resource missing; nothing is written.
        if (body.Exists == null)
        {
            await SendErrorsAsync(400, ct);
            return;
        }

        Guid networkId = Route<Guid>("networkId");
        await handler.HandleAsync(new ObserveNetworkCommand(networkId, body.Exists.Value, body.Observed?.Status), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; ObserveNetworkCommand additionally carries the route id.
// Exists is required; nullable so a body without it reaches the 400 above.
public record ObserveNetworkBody(bool? Exists, NetworkObservedBody? Observed = null);

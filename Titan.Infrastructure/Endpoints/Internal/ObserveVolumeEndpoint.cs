using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler's observation of one volume (titan-ops architecture/resource-lifecycle.md#callback-routes): whether Incus
// has it and what uses it. Ends no request.
public class ObserveVolumeEndpoint(ObserveVolumeCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveVolumeBody>
{
    public override void Configure()
    {
        Patch("internal/volumes/{volumeId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveVolumeBody body, CancellationToken ct)
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

        Guid volumeId = Route<Guid>("volumeId");
        await handler.HandleAsync(new ObserveVolumeCommand(volumeId, body.Exists.Value, body.Observed?.AttachedTo), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; ObserveVolumeCommand additionally carries the route id.
// Exists is required; nullable so a body without it reaches the 400 above.
public record ObserveVolumeBody(bool? Exists, VolumeObservedBody? Observed = null);

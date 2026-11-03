using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a volume request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes):
// the route names the volume and the request, the body says how it ended and what the worker saw. 204 also when the
// volume is not waiting on that request: the worker has nothing to retry.
public class EndVolumeRequestEndpoint(EndVolumeRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndVolumeRequestBody>
{
    public override void Configure()
    {
        Patch("internal/volumes/{volumeId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndVolumeRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid volumeId = Route<Guid>("volumeId");
        Guid requestId = Route<Guid>("requestId");
        // An observation without names is a volume Incus has and nothing uses.
        List<string>? attachedTo = body.Observed == null ? null : body.Observed.AttachedTo ?? [];
        await handler.HandleAsync(new EndVolumeRequestCommand(
            volumeId, requestId, body.Outcome, body.ExternalId, attachedTo, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; EndVolumeRequestCommand additionally carries the two route ids.
public record EndVolumeRequestBody(
    string Outcome,
    string? ExternalId = null,
    VolumeObservedBody? Observed = null,
    string? Reason = null,
    string? Detail = null);

// What the worker read of the volume: the names of the instances in its used_by, word for word, none when it is free.
// Shared by the request report and the reconciler's observation.
public record VolumeObservedBody(List<string>? AttachedTo = null);

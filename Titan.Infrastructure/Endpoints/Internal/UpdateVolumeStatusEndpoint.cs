using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// {volumeId} rides the route (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record UpdateVolumeStatusBody(
    string Report,
    bool? Exists = null,
    List<string>? AttachedTo = null,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

public class UpdateVolumeStatusEndpoint(
    UpdateVolumeStatusCommandHandler handler,
    IConfiguration config) : Endpoint<UpdateVolumeStatusBody>
{
    public override void Configure()
    {
        Patch("internal/volumes/{volumeId:guid}/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(UpdateVolumeStatusBody body, CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var volumeId = Route<Guid>("volumeId");
        await handler.HandleAsync(
            new UpdateVolumeStatusCommand(volumeId, body.Report, body.Exists, body.AttachedTo, body.ExternalId,
                body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

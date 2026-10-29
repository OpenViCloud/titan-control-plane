using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Infrastructure.Endpoints.Internal;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

// Both ids ride the route (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record UpdateVolumeSnapshotStatusBody(
    string Report,
    bool? Exists = null,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

public class UpdateVolumeSnapshotStatusEndpoint(
    UpdateVolumeSnapshotStatusCommandHandler handler,
    IConfiguration config) : Endpoint<UpdateVolumeSnapshotStatusBody>
{
    public override void Configure()
    {
        Patch("internal/volumes/{volumeId:guid}/snapshots/{snapshotId:guid}/status");
        AllowAnonymous();
    }

    public override async Task HandleAsync(UpdateVolumeSnapshotStatusBody body, CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var volumeId = Route<Guid>("volumeId");
        var snapshotId = Route<Guid>("snapshotId");
        await handler.HandleAsync(
            new UpdateVolumeSnapshotStatusCommand(
                volumeId, snapshotId, body.Report, body.Exists, body.ExternalId, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

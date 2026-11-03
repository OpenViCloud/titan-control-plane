using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Infrastructure.Endpoints.Internal;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

// The worker reports that a volume snapshot request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes). 204 also when the snapshot is not waiting on that
// request.
public class EndVolumeSnapshotRequestEndpoint(EndVolumeSnapshotRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndVolumeSnapshotRequestBody>
{
    public override void Configure()
    {
        Patch("internal/volumes/{volumeId:guid}/snapshots/{snapshotId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndVolumeSnapshotRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        await handler.HandleAsync(new EndVolumeSnapshotRequestCommand(
            Route<Guid>("volumeId"), Route<Guid>("snapshotId"), Route<Guid>("requestId"),
            body.Outcome, body.ExternalId, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the three route ids.
public record EndVolumeSnapshotRequestBody(
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

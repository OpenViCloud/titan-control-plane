using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Infrastructure.Endpoints.Internal;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

// The reconciler's observation of one volume snapshot (titan-ops architecture/resource-lifecycle.md#callback-routes):
// whether Incus has it. Writes current only.
public class ObserveVolumeSnapshotEndpoint(ObserveVolumeSnapshotCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveVolumeSnapshotBody>
{
    public override void Configure()
    {
        Patch("internal/volumes/{volumeId:guid}/snapshots/{snapshotId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveVolumeSnapshotBody body, CancellationToken ct)
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

        await handler.HandleAsync(
            new ObserveVolumeSnapshotCommand(Route<Guid>("volumeId"), Route<Guid>("snapshotId"), body.Exists.Value), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; the command additionally carries the two route ids.
// Exists is required; nullable so a body without it reaches the 400 above.
public record ObserveVolumeSnapshotBody(bool? Exists);

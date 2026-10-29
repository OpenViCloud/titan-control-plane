using Microsoft.AspNetCore.Http;
using Titan.Infrastructure.Auth;
using FastEndpoints;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

// Both ids ride the route, so the BODY record carries only Name (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record RestoreVolumeSnapshotBody(string Name);

public class RestoreVolumeSnapshotEndpoint(RestoreVolumeSnapshotCommandHandler handler)
    : Endpoint<RestoreVolumeSnapshotBody, RestoreVolumeSnapshotCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/volumes/{volumeId:guid}/snapshots/{snapshotId:guid}/restore");
        Description(b => b.ClearDefaultProduces(200).Produces<RestoreVolumeSnapshotCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(RestoreVolumeSnapshotBody body, CancellationToken ct)
    {
        var volumeId = Route<Guid>("volumeId");
        var snapshotId = Route<Guid>("snapshotId");
        var result = await handler.HandleAsync(
            new RestoreVolumeSnapshotCommand(volumeId, snapshotId, body.Name), ct);
        await SendAsync(result, 202, ct);
    }
}

using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

public class DeleteVolumeSnapshotEndpoint(DeleteVolumeSnapshotCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/tenant/volumes/{volumeId:guid}/snapshots/{snapshotId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var volumeId = Route<Guid>("volumeId");
        var snapshotId = Route<Guid>("snapshotId");
        await handler.HandleAsync(new DeleteVolumeSnapshotCommand(volumeId, snapshotId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body, not `{}`
    }
}

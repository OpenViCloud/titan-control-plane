using Microsoft.AspNetCore.Http;
using Titan.Infrastructure.Auth;
using FastEndpoints;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

// {volumeId} rides the route, so the BODY record carries only Name (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateVolumeSnapshotBody(string Name);

public class CreateVolumeSnapshotEndpoint(CreateVolumeSnapshotCommandHandler handler)
    : Endpoint<CreateVolumeSnapshotBody, CreateVolumeSnapshotCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/volumes/{volumeId:guid}/snapshots");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateVolumeSnapshotCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(CreateVolumeSnapshotBody body, CancellationToken ct)
    {
        var volumeId = Route<Guid>("volumeId");
        var result = await handler.HandleAsync(new CreateVolumeSnapshotCommand(volumeId, body.Name), ct);
        await SendAsync(result, 202, ct);
    }
}

using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;

namespace Titan.Infrastructure.Endpoints.Volume;

public class DeleteVolumeEndpoint(DeleteVolumeCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/tenant/volumes/{volumeId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var volumeId = Route<Guid>("volumeId");
        await handler.HandleAsync(new DeleteVolumeCommand(volumeId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body, not `{}`
    }
}

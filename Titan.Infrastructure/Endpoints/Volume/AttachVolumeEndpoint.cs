using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;

namespace Titan.Infrastructure.Endpoints.Volume;

// {volumeId} rides the route, so the BODY record carries only what the body actually holds
// (titan-ops architecture/code-structure.md#command-is-wire-contract); the endpoint composes both into the command.
public record AttachVolumeBody(Guid VmId);

public class AttachVolumeEndpoint(AttachVolumeCommandHandler handler) : Endpoint<AttachVolumeBody>
{
    public override void Configure()
    {
        Post("v1/tenant/volumes/{volumeId:guid}/attach");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(AttachVolumeBody body, CancellationToken ct)
    {
        var volumeId = Route<Guid>("volumeId");
        await handler.HandleAsync(new AttachVolumeCommand(volumeId, body.VmId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body, not `{}`
    }
}

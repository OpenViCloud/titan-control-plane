using Microsoft.AspNetCore.Http;
using Titan.Infrastructure.Auth;
using FastEndpoints;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;

namespace Titan.Infrastructure.Endpoints.Volume;

// The command IS the request type (titan-ops architecture/code-structure.md#command-is-wire-contract), so FastEndpoints binds the body straight to it.
public class CreateVolumeEndpoint(CreateVolumeCommandHandler handler)
    : Endpoint<CreateVolumeCommand, CreateVolumeCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/volumes");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateVolumeCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(CreateVolumeCommand cmd, CancellationToken ct)
    {
        var result = await handler.HandleAsync(cmd, ct);
        await SendAsync(result, 202, ct);
    }
}

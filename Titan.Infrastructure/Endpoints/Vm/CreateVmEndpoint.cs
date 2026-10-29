using Microsoft.AspNetCore.Http;
using Titan.Infrastructure.Auth;
using FastEndpoints;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Vm;

// The command IS the request type (titan-ops architecture/code-structure.md#contracts), so FastEndpoints binds the body straight to it.
public class CreateVmEndpoint(CreateVmCommandHandler handler)
    : Endpoint<CreateVmCommand, CreateVmCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/vms");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateVmCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(CreateVmCommand cmd, CancellationToken ct)
    {
        var result = await handler.HandleAsync(cmd, ct);
        await SendAsync(result, 202, ct);
    }
}

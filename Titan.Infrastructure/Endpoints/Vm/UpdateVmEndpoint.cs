using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Vm;

// The command side owns no read shape: a caller that wants the renamed VM reads GET /v1/tenant/vms/{vmId}
// (titan-ops architecture/http-contract.md#commands-return-no-data).
public class UpdateVmEndpoint(RenameVmCommandHandler handler) : Endpoint<RenameVmBody>
{
    public override void Configure()
    {
        Patch("v1/tenant/vms/{vmId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(204));
    }

    public override async Task HandleAsync(RenameVmBody body, CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        await handler.HandleAsync(new RenameVmCommand(vmId, body.Name), ct);
        await SendNoContentAsync(ct);
    }
}

// The JSON body alone; RenameVmCommand additionally carries the route id
// (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record RenameVmBody(string Name);

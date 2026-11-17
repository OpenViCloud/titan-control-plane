using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Vm;

// The command side owns no read shape: a caller that wants the changed VM reads GET /v1/tenant/vms/{vmId}
// (titan-ops architecture/http-contract.md#commands-return-no-data).
public class UpdateVmEndpoint(UpdateVmCommandHandler handler) : Endpoint<UpdateVmBody>
{
    public override void Configure()
    {
        Patch("v1/tenant/vms/{vmId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(204));
    }

    public override async Task HandleAsync(UpdateVmBody body, CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        await handler.HandleAsync(new UpdateVmCommand(vmId, body.Name, body.StopTimeoutSeconds), ct);
        await SendNoContentAsync(ct);
    }
}

// The JSON body alone; UpdateVmCommand additionally carries the route id
// (titan-ops architecture/code-structure.md#command-is-wire-contract). A body of a name alone is the rename it always was.
public record UpdateVmBody(string? Name = null, int? StopTimeoutSeconds = null);

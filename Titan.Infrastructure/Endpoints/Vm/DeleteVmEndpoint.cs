using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Vm;

public class DeleteVmEndpoint(DeleteVmCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/tenant/vms/{vmId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        await handler.HandleAsync(new DeleteVmCommand(vmId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body, not `{}`
    }
}

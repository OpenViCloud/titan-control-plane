using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Vm;

// The status guard lives in the aggregate (titan-ops features/vm/design.md#vm-status).
public class StartVmEndpoint(StartVmCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("v1/tenant/vms/{vmId:guid}/start");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        await handler.HandleAsync(new StartVmCommand(vmId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body, not `{}`
    }
}

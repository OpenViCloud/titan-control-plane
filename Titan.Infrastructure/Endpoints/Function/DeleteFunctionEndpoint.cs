using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Function;

// 202 whether the delete waits for the worker or was done at once (a function never deployed), as a load balancer's.
public class DeleteFunctionEndpoint(DeleteFunctionCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/tenant/functions/{functionId:guid}");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteFunctionCommand(Route<Guid>("functionId")), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted)); // titan-ops architecture/http-contract.md#async-actions-return-202: no body
    }
}

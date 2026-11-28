using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Infrastructure.Auth;

namespace Titan.Infrastructure.Endpoints.Function;

// 201: a function is made Titan-side only, nothing waits for the cluster (titan-ops architecture/http-contract.md#create-returns-id).
public class CreateFunctionEndpoint(CreateFunctionCommandHandler handler)
    : Endpoint<CreateFunctionCommand, CreateFunctionCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/functions");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateFunctionCommandResult>(201, "application/json"));
    }

    public override async Task HandleAsync(CreateFunctionCommand command, CancellationToken ct)
    {
        var result = await handler.HandleAsync(command, ct);
        await SendAsync(result, 201, ct);
    }
}

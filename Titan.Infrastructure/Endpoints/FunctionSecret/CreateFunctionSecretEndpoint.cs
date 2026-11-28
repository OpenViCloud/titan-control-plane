using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.FunctionSecret;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Infrastructure.Auth;

namespace Titan.Infrastructure.Endpoints.FunctionSecret;

// titan-ops features/ovc-function/design.md#function-secrets: the secret and its version 1, Titan-side only.
public class CreateFunctionSecretEndpoint(CreateFunctionSecretCommandHandler handler)
    : Endpoint<CreateFunctionSecretCommand, CreateFunctionSecretCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/function-secrets");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateFunctionSecretCommandResult>(201, "application/json"));
    }

    public override async Task HandleAsync(CreateFunctionSecretCommand command, CancellationToken ct)
    {
        var result = await handler.HandleAsync(command, ct);
        await SendAsync(result, 201, ct);
    }
}

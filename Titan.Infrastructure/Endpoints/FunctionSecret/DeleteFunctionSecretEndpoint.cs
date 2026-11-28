using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.FunctionSecret;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;

namespace Titan.Infrastructure.Endpoints.FunctionSecret;

public class DeleteFunctionSecretEndpoint(DeleteFunctionSecretCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("v1/tenant/function-secrets/{name}");
        Description(b => b.ClearDefaultProduces(200).Produces(204));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await handler.HandleAsync(new DeleteFunctionSecretCommand(Route<string>("name")!), ct);
        await SendNoContentAsync(ct);
    }
}

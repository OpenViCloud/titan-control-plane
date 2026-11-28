using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;

namespace Titan.Infrastructure.Endpoints.Function;

// 202: the worker deploys later (titan-ops architecture/http-contract.md#async-actions-return-202). A repeat of the
// waiting or running version answers the same.
public class DeployFunctionEndpoint(DeployFunctionCommandHandler handler) : Endpoint<DeployFunctionBody>
{
    public override void Configure()
    {
        Post("v1/tenant/functions/{functionId:guid}/deploy");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(DeployFunctionBody body, CancellationToken ct)
    {
        await handler.HandleAsync(new DeployFunctionCommand(Route<Guid>("functionId"), body.VersionId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted));
    }
}

// The JSON body; DeployFunctionCommand additionally carries the route's function id.
public record DeployFunctionBody(Guid VersionId);

public class DeployFunctionValidator : Validator<DeployFunctionBody>
{
    public DeployFunctionValidator()
    {
        RuleFor(x => x.VersionId).NotEmpty();
    }
}

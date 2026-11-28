using FastEndpoints;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Compute.FunctionSecret;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Infrastructure.Auth;

namespace Titan.Infrastructure.Endpoints.FunctionSecret;

// A new value, as a new version (titan-ops features/ovc-function/design.md#function-secrets): a POST that makes a row,
// so it answers 201 with the number and takes an Idempotency-Key like every create.
public class AddFunctionSecretVersionEndpoint(AddFunctionSecretVersionCommandHandler handler)
    : Endpoint<AddFunctionSecretVersionBody, AddFunctionSecretVersionCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/function-secrets/{name}/versions");
        Description(b => b.ClearDefaultProduces(200).Produces<AddFunctionSecretVersionCommandResult>(201, "application/json"));
    }

    public override async Task HandleAsync(AddFunctionSecretVersionBody body, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AddFunctionSecretVersionCommand(Route<string>("name")!, body.Value!), ct);
        await SendAsync(result, 201, ct);
    }
}

public class AddFunctionSecretVersionValidator : Validator<AddFunctionSecretVersionBody>
{
    public AddFunctionSecretVersionValidator()
    {
        RuleFor(x => x.Value).IsASecretValue();
    }
}

// The JSON body; AddFunctionSecretVersionCommand additionally carries the route's name.
public record AddFunctionSecretVersionBody(string? Value)
{
    // A record prints every property: the value must never reach a log through ToString.
    protected virtual bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Value = ");
        builder.Append(Value == null ? "null" : "***");
        return true;
    }
}

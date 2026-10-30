using Microsoft.AspNetCore.Http;
using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Platform.ApiToken;
using Titan.Application.Commands.Platform.ApiToken.Contracts;
using Titan.Infrastructure.Auth;
using ApiTokenAggregate = Titan.Domain.Aggregates.ApiToken.ApiToken;

namespace Titan.Infrastructure.Endpoints.ApiToken;

public class CreateApiTokenValidator : Validator<CreateApiTokenCommand>
{
    public CreateApiTokenValidator()
    {
        RuleFor(x => x.Name)
            .Must(HasAcceptedLength)
            .WithMessage($"Name must be 1 to {ApiTokenAggregate.MaxNameLength} characters.");
        RuleFor(x => x.Token)
            .Must(token => token != null && ApiTokenAggregate.IsWellFormed(token))
            .WithMessage("Token must be ovc_ followed by 43 base64url characters.");
        RuleFor(x => x.ExpiresInDays)
            .Must(days => days == null || ApiTokenAggregate.Lifetimes.Contains(days.Value))
            .WithMessage("ExpiresInDays must be 30, 90 or 365, or null for a token that never expires.");
    }

    private static bool HasAcceptedLength(string? name)
    {
        if (name == null)
        {
            return false;
        }

        int length = name.Trim().Length;
        return length > 0 && length <= ApiTokenAggregate.MaxNameLength;
    }
}

public class CreateApiTokenEndpoint(CreateApiTokenCommandHandler handler)
    : Endpoint<CreateApiTokenCommand, CreateApiTokenCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/api-tokens");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateApiTokenCommandResult>(201, "application/json"));
    }

    public override async Task HandleAsync(CreateApiTokenCommand command, CancellationToken ct)
    {
        var result = await handler.HandleAsync(command, ct);
        await SendAsync(result, 201, ct);
    }
}

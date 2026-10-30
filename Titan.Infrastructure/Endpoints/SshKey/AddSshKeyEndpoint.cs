using Microsoft.AspNetCore.Http;
using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.SshKey;
using Titan.Application.Commands.Compute.SshKey.Contracts;
using Titan.Domain.Aggregates.SshKey;
using Titan.Infrastructure.Auth;
using SshKeyAggregate = Titan.Domain.Aggregates.SshKey.SshKey;

namespace Titan.Infrastructure.Endpoints.SshKey;

public class AddSshKeyValidator : Validator<AddSshKeyCommand>
{
    public AddSshKeyValidator()
    {
        RuleFor(x => x.Name)
            .Must(HasAcceptedLength)
            .WithMessage($"Name must be 1 to {SshKeyAggregate.MaxNameLength} characters.");
        RuleFor(x => x.PublicKey)
            .Custom((publicKey, context) =>
            {
                string? problem = SshPublicKey.FindProblem(publicKey ?? "");
                if (problem != null)
                {
                    context.AddFailure(nameof(AddSshKeyCommand.PublicKey), problem);
                }
            });
    }

    private static bool HasAcceptedLength(string? name)
    {
        if (name == null)
        {
            return false;
        }

        int length = name.Trim().Length;
        return length > 0 && length <= SshKeyAggregate.MaxNameLength;
    }
}

public class AddSshKeyEndpoint(AddSshKeyCommandHandler handler)
    : Endpoint<AddSshKeyCommand, AddSshKeyCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/ssh-keys");
        Description(b => b.ClearDefaultProduces(200).Produces<AddSshKeyCommandResult>(201, "application/json"));
    }

    public override async Task HandleAsync(AddSshKeyCommand command, CancellationToken ct)
    {
        var result = await handler.HandleAsync(command, ct);
        await SendAsync(result, 201, ct);
    }
}

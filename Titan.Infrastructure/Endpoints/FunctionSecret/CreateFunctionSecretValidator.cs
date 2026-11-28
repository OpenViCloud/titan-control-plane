using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;

namespace Titan.Infrastructure.Endpoints.FunctionSecret;

// The name is an environment variable's (titan-ops features/ovc-function/design.md#function-env).
public class CreateFunctionSecretValidator : Validator<CreateFunctionSecretCommand>
{
    public CreateFunctionSecretValidator()
    {
        RuleFor(x => x.Name)
            .Must(FunctionSecretAggregate.IsAName)
            .WithMessage($"'name' is 1 to {FunctionSecretAggregate.NameMaxLength} letters, digits or underscores, not starting with a digit.");
        RuleFor(x => x.Value).IsASecretValue();
    }
}

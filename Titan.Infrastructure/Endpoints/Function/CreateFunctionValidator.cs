using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.Function.Contracts;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Infrastructure.Endpoints.Function;

// The rule is the Domain's (titan-ops architecture/domain-methods.md#names-and-sizes); this answers 400 in the API's words.
public class CreateFunctionValidator : Validator<CreateFunctionCommand>
{
    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(FunctionAggregate.NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    public CreateFunctionValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(FunctionAggregate.NameMaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, hyphens, and underscores, and must start with a letter or digit.");
        RuleFor(x => x.ClusterId).NotEmpty();
    }
}

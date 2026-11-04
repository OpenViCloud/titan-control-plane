using FastEndpoints;
using FluentValidation;

using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Infrastructure.Endpoints.Vm;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement); this answers 400 in the API's words.
public class UpdateVmValidator : Validator<RenameVmBody>
{
    private static readonly System.Text.RegularExpressions.Regex NameRegex =
        new(VmAggregate.NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    public UpdateVmValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VmAggregate.NameMaxLength)
            .Matches(NameRegex).WithMessage("'Name' may only contain letters, digits, and hyphens, and must start with a letter or digit.");
    }
}

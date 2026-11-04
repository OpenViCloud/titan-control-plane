using FastEndpoints;
using FluentValidation;

using VmSnapshotRule = Titan.Domain.Aggregates.Vm.VmSnapshot;

namespace Titan.Infrastructure.Endpoints.VmSnapshots;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement); this answers 400 in the API's words.
public class CreateSnapshotValidator : Validator<CreateSnapshotBody>
{
    public CreateSnapshotValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VmSnapshotRule.NameMaxLength);
    }
}

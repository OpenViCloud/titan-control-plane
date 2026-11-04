using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.Volume.Contracts;

using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Infrastructure.Endpoints.Volume;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement); this answers 400 in the API's words.
public class CreateVolumeValidator : Validator<CreateVolumeCommand>
{
    public CreateVolumeValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VolumeAggregate.NameMaxLength);
        RuleFor(x => x.VolumeOfferingId).NotEmpty();
        RuleFor(x => x.ClusterId).NotEmpty();
        // No upper cap yet: only the non-positive size is refused here.
        RuleFor(x => x.SizeGb).GreaterThan(0);
    }
}

using FastEndpoints;
using FluentValidation;

using VolumeAggregateRule = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement); this answers 400 in the API's words.
public class RestoreVolumeSnapshotValidator : Validator<RestoreVolumeSnapshotBody>
{
    public RestoreVolumeSnapshotValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VolumeAggregateRule.NameMaxLength);
    }
}

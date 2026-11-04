using FastEndpoints;
using FluentValidation;

using VolumeSnapshotRule = Titan.Domain.Aggregates.Volume.VolumeSnapshot;

namespace Titan.Infrastructure.Endpoints.VolumeSnapshots;

// The rule is the Domain's (titan-ops architecture/code-structure.md#logic-placement); this answers 400 in the API's words.
public class CreateVolumeSnapshotValidator : Validator<CreateVolumeSnapshotBody>
{
    public CreateVolumeSnapshotValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(VolumeSnapshotRule.NameMaxLength);
    }
}

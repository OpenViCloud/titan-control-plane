using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Compute.Volume.Contracts;

namespace Titan.Infrastructure.Endpoints.Volume;

public class CreateVolumeValidator : Validator<CreateVolumeCommand>
{
    public CreateVolumeValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.VolumeOfferingId).NotEmpty();
        RuleFor(x => x.ClusterId).NotEmpty();
        // No upper cap yet: only the non-positive size is refused here.
        RuleFor(x => x.SizeGb).GreaterThan(0);
    }
}

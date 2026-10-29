using FastEndpoints;
using FluentValidation;
using Titan.Application.Commands.Catalog.VmOffering;
using Titan.Application.Commands.Catalog.VmOffering.Contracts;

namespace Titan.Infrastructure.Endpoints.Catalog;

// titan-ops architecture/money-and-wallet.md#price-belongs-to-offering
public class UpdateVmOfferingPriceEndpoint(UpdateVmOfferingPriceCommandHandler handler)
    : Endpoint<UpdateVmOfferingPriceRequest>
{
    public override void Configure()
    {
        Patch("v1/admin/vm-offerings/{offeringId}/price");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(UpdateVmOfferingPriceRequest req, CancellationToken ct)
    {
        var offeringId = Route<string>("offeringId")!;
        await handler.HandleAsync(new UpdateVmOfferingPriceCommand(offeringId, req.PricePerHour), ct);
        await SendNoContentAsync(ct);
    }
}

// The JSON body; UpdateVmOfferingPriceCommand additionally carries the route's offering id (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record UpdateVmOfferingPriceRequest(decimal PricePerHour);

public class UpdateVmOfferingPriceValidator : Validator<UpdateVmOfferingPriceRequest>
{
    public UpdateVmOfferingPriceValidator()
    {
        // VmOffering.ChangePrice guards this too, but with ArgumentException, which the exception handler maps
        // to 500: an admin's typo dressed up as a server fault. Caught here as 400 VALIDATION_ERROR.
        RuleFor(x => x.PricePerHour)
            .GreaterThan(0m)
            .WithMessage("pricePerHour must be greater than 0");
    }
}

using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmOffering.Contracts;
using Titan.Domain.Aggregates.Platform.AdminAuditLog;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Commands.Catalog.VmOffering;

/// Admin price change (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering), recorded in the append-only
/// admin audit trail. Returns nothing (titan-ops architecture/http-contract.md#commands-return-no-data).
public class UpdateVmOfferingPriceCommandHandler(
    IVmOfferingRepository repo,
    IAdminAuditLogRepository auditLog,
    ICurrentUser currentUser,
    ILogger<UpdateVmOfferingPriceCommandHandler> logger)
{
    /// A constant so every reader that filters on it matches the same way.
    public const string PriceChangeAction = "pricing.vm_offering.change";
    public const string PriceChangeResourceType = "vm_offering";

    public async Task HandleAsync(UpdateVmOfferingPriceCommand command, CancellationToken ct = default)
    {
        var (id, pricePerHour) = command;

        var offering = await repo.FindByIdAsync(id, ct)
            ?? throw new NotFoundException("VM_OFFERING_NOT_FOUND", $"VM offering '{id}' does not exist.");

        var oldPrice = offering.PricePerHour;
        offering.ChangePrice(pricePerHour);

        // Staged on the same DbContext as the offering, so the audit row and the new price commit
        // together: a price that changed with no trace, or a trace of a change that did not happen,
        // are both worse than failing.
        auditLog.Add(AdminAuditLog.CreatePlatform(
            PriceChangeAction, currentUser.KeycloakUserId, PriceChangeResourceType,
            // This detail is persisted, so it must not depend on the host's culture: `$"{decimal}"` formats
            // per current culture, and an en_VN host writes "311.256" as "311,256". oldPrice is a Rate, whose
            // ToString() is invariant; pricePerHour is a decimal, so it is pinned explicitly here.
            resourceKey: id,
            detail: $"{oldPrice} -> {pricePerHour.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        await repo.SaveAsync(ct);

        logger.LogInformation(
            "VM offering {VmOfferingId} price changed {OldPrice} -> {NewPrice} by {Actor}",
            id, oldPrice, pricePerHour, currentUser.KeycloakUserId);
    }
}

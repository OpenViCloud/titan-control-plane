using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Application.Exceptions;

namespace Titan.Application.Commands.Platform.Tenant;

/// Platform-admin suspends a tenant, or rejects a pending signup. Active is Billing's ActivateTenantCommandHandler, which
/// opens the wallet in the same save (titan-ops architecture/tenancy-and-admin.md#approval-is-activation); the endpoint
/// sends it there.
public class UpdateTenantStatusCommandHandler(ITenantRepository repo)
{
    /// The one reading of the admin's status string: the endpoint reads it once and sends Active to the handler that
    /// opens the wallet. Only an enum name, any case; Enum.TryParse alone would also take "1" or " Active".
    public static TenantStatus ParseTarget(string status)
    {
        bool isAName = !string.IsNullOrEmpty(status) && status.All(char.IsLetter);
        if (!isAName || !Enum.TryParse<TenantStatus>(status, ignoreCase: true, out var parsed))
        {
            throw new BadRequestException("INVALID_STATUS", $"Invalid status '{status}'.");
        }
        return parsed;
    }

    public async Task HandleAsync(UpdateTenantStatusCommand command, CancellationToken ct = default)
    {
        var tenant = await repo.FindByIdAsync(command.TenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        if (command.Status == TenantStatus.Active)
        {
            throw new InvalidOperationException("Active goes to ActivateTenantCommandHandler, which opens the wallet.");
        }

        // Only two targets exist for an admin: Active and Suspended; anything else is refused.
        if (command.Status != TenantStatus.Suspended)
        {
            throw new BadRequestException("INVALID_STATUS", $"A tenant can only be set to {TenantStatus.Active} or {TenantStatus.Suspended}.");
        }

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): already suspended answers 204, saves nothing.
        if (tenant.IsSuspended)
        {
            return;
        }

        tenant.Suspend(); // also how a pending signup is rejected — no provider touched
        await repo.SaveAsync(ct);
    }
}

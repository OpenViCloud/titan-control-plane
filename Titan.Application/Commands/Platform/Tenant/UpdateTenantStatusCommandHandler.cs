using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Commands.Platform.Tenant;

/// Platform-admin suspends or activates a tenant, and decides a pending signup. Activating opens no wallet here:
/// the endpoint opens it first (titan-ops architecture/tenancy-and-admin.md#approval-is-activation).
public class UpdateTenantStatusCommandHandler(ITenantRepository repo)
{
    /// The one reading of the admin's status string, shared with the endpoint that opens the wallet first: if the two
    /// disagreed on what "Active" is, a tenant could become Active without a wallet. Only an enum name, any case;
    /// Enum.TryParse alone would also take "1" or " Active".
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
        var (tenantId, status) = command;

        var tenant = await repo.FindByIdAsync(tenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        TenantStatus parsed = ParseTarget(status);

        // Only two targets exist for an admin: Active and Suspended; anything else is refused.
        if (parsed == TenantStatus.Suspended)
        {
            // The aggregate refuses a repeat, so the handler recognises it first
            // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): already suspended answers 204, saves nothing.
            bool alreadySuspended = tenant.Status == TenantStatus.Suspended;
            if (alreadySuspended)
            {
                return;
            }

            tenant.Suspend(); // also how a pending signup is rejected — no provider touched
            await repo.SaveAsync(ct);
            return;
        }

        if (parsed != TenantStatus.Active)
        {
            throw new BadRequestException("INVALID_STATUS", $"A tenant can only be set to {TenantStatus.Active} or {TenantStatus.Suspended}.");
        }

        // A press after the tenant is already Active is the same request with its result already there
        // (titan-ops architecture/intent-and-observation.md#intent-has-identity): answer 204, save nothing.
        bool alreadyActive = tenant.Status == TenantStatus.Active;
        if (alreadyActive)
        {
            return;
        }

        // A pending, rejected or suspended tenant becomes Active.
        tenant.RequestActivation();
        await repo.SaveAsync(ct);
    }
}

using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Commands.Platform.Tenant;

/// A PendingApproval tenant of an account the realm declares becomes Active, without any admin action. The middleware
/// opens the wallet before calling this (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#approval-is-activation).
/// No-op if the tenant is missing or no longer PendingApproval.
public class ApproveDeclaredAccountCommandHandler(ITenantRepository repo)
{
    public async Task HandleAsync(ApproveDeclaredAccountCommand command, CancellationToken ct = default)
    {
        Domain.Aggregates.Platform.Tenant.Tenant? tenant = await repo.FindByIdAsync(command.TenantId, ct);
        if (tenant == null)
        {
            return; // gone — the middleware asked for a tenant that no longer exists
        }

        // Repeat (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat): two requests of a declared account
        // can race. Either way it is already approved.
        bool alreadyApproved = tenant.Status != TenantStatus.PendingApproval;
        if (alreadyApproved)
        {
            return;
        }

        tenant.Approve();
        await repo.SaveAsync(ct);
    }
}

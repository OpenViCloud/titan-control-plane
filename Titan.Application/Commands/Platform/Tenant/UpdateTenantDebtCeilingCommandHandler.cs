using Titan.Application.Commands.Platform.Tenant.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Platform;

namespace Titan.Application.Commands.Platform.Tenant;

/// A reference number the admin reads at month-end close (titan-ops architecture/money-and-wallet.md#postpaid-no-brake):
/// it gates nothing, not even creation.
public class UpdateTenantDebtCeilingCommandHandler(ITenantRepository repo)
{
    public async Task HandleAsync(UpdateTenantDebtCeilingCommand command, CancellationToken ct = default)
    {
        var (tenantId, debtCeiling) = command;

        var tenant = await repo.FindByIdAsync(tenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        tenant.UpdateDebtCeiling((long)debtCeiling);
        await repo.SaveAsync(ct);
    }
}

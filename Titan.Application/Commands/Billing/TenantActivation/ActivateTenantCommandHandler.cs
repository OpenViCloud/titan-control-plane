using Titan.Application.Commands.Billing.TenantActivation.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using ActivationService = Titan.Domain.Services.TenantActivation;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Billing.TenantActivation;

/// The platform admin's Active: a pending, rejected or suspended tenant becomes Active, with its wallet
/// (titan-ops architecture/tenancy-and-admin.md#approval-is-activation). In Billing, not Platform: it opens the wallet, and
/// Platform reading Billing would be a module cycle.
public class ActivateTenantCommandHandler(
    ITenantRepository tenants,
    IWalletRepository wallets,
    ActivationService activation,
    CreditOptions options)
{
    public async Task HandleAsync(ActivateTenantCommand command, CancellationToken ct = default)
    {
        TenantAggregate tenant = await tenants.FindByIdForUpdateAsync(command.TenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        // A press after the tenant is already Active is the same request with its result already there
        // (titan-ops architecture/intent-and-observation.md#intent-has-identity): answer 204, save nothing.
        bool alreadyActive = tenant.Status == TenantStatus.Active;
        if (alreadyActive)
        {
            return;
        }

        await activation.ActivateAsync(tenant, options.Currency, ct);
        // The wallet repository's save, since the wallet is the one insert here.
        await wallets.SaveChangesAsync(ct);
    }
}

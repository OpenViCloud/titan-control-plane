using Titan.Application.Commands.Billing.TenantActivation.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Repositories;
using ActivationService = Titan.Domain.Services.TenantActivation;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Billing.TenantActivation;

/// A PendingApproval tenant whose account has the realm role auto-approve becomes Active, with its wallet, without any
/// admin action (titan-ops architecture/tenancy-and-admin.md#approval-is-activation). In Billing, not Platform: it opens the
/// wallet, and Platform reading Billing would be a module cycle. No-op if the tenant is missing or no longer pending.
public class ApproveDeclaredAccountCommandHandler(
    ITenantRepository tenants,
    IWalletRepository wallets,
    ActivationService activation,
    CreditOptions options)
{
    public async Task HandleAsync(ApproveDeclaredAccountCommand command, CancellationToken ct = default)
    {
        // Two first requests of one account take turns at the lock. The second finds the tenant no longer pending and leaves
        // it alone (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat); a request that bootstrapped the
        // tenant still holds its own pending copy, approves it again and finds the wallet the first opened.
        TenantAggregate? tenant = await tenants.FindByIdForUpdateAsync(command.TenantId, ct);
        if (tenant != null && tenant.Status == TenantStatus.PendingApproval)
        {
            await activation.ApproveAsync(tenant, options.Currency, ct);
        }

        // Also when nothing changed: the save ends the lock's transaction, and the request goes on past the middleware.
        // The wallet repository's save, since the wallet is the one insert here.
        await wallets.SaveChangesAsync(ct);
    }
}

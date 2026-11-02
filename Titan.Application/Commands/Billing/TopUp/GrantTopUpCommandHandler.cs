using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.TopUp.Contracts;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Application.Commands.Billing.TopUp;

/// Free credit, effective immediately: a TopUp born Confirmed with Source=PromotionalCredit, crediting
/// the wallet with a Promo ledger movement.
public class GrantTopUpCommandHandler(
    IWalletRepository walletRepo,
    TopUpCredit topUpCredit,
    ICurrentUser currentUser,
    CreditOptions options)
{
    public async Task<GrantTopUpCommandResult> HandleAsync(GrantTopUpCommand command, CancellationToken ct = default)
    {
        var (tenantId, amount, reason) = (command.TenantId, command.Amount, command.Reason);

        // The factory guards amount > 0; the max cap is a policy (config), so it lives here rather than
        // in the aggregate — it protects against an admin typo (500.000 vs 50.000.000), not an invariant.
        if (amount > (long)options.MaxGrantAmount)
            throw new BusinessException("GRANT_AMOUNT_EXCEEDS_MAX",
                $"A single promotional credit may not exceed {options.MaxGrantAmount:N0} {options.Currency}.");

        var topUp = await topUpCredit.GrantPromoAsync(tenantId, amount, options.Currency, reason, currentUser.KeycloakUserId, ct);
        try
        {
            await walletRepo.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // SaveChanges rolled its own transaction back — nothing written; surface for retry.
            throw new ConflictException("GRANT_CONFLICT",
                $"Granting credit to tenant {tenantId} hit a concurrent wallet change; retry.");
        }

        return new GrantTopUpCommandResult(topUp.Id);
    }
}

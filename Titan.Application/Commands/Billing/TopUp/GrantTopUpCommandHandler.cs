using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.TopUp.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Billing;
using TopUpAggregate = Titan.Domain.Aggregates.Billing.TopUp.TopUp;

namespace Titan.Application.Commands.Billing.TopUp;

/// Free credit, effective immediately: a TopUp born Confirmed with Source=PromotionalCredit, crediting
/// the wallet with a Promo ledger movement.
public class GrantTopUpCommandHandler(
    ICreditRepository repo,
    IWalletRepository walletRepo,
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

        var topUp = TopUpAggregate.GrantPromo(tenantId, amount, options.Currency, reason, currentUser.KeycloakUserId);

        // The grant row and the money commit in one SaveChanges. A Promo ledger movement keeps free credit
        // separable from real money (titan-ops architecture/money-and-wallet.md#tax-legal-posture).
        var wallet = await walletRepo.GetByTenantForUpdateAsync(tenantId, ct)
            ?? throw new NotFoundException("WALLET_NOT_FOUND",
                $"Tenant {tenantId} has no wallet; it should have been opened at activation.");
        wallet.Credit(amount);
        repo.AddTopUp(topUp);
        walletRepo.AddTransaction(
            WalletTransaction.ForPromo(wallet.Id, amount, topUp.Id, wallet.AvailableAmount));
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

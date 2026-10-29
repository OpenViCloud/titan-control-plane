using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Queries.Billing.Wallet.Contracts;
using Titan.Domain.Repositories.Billing;

namespace Titan.Application.Queries.Billing.Wallet;

/// What the tenant can spend right now.
public class GetWalletQueryHandler(IWalletRepository walletRepo, ICurrentUser currentUser, CreditOptions options)
{
    public async Task<WalletQueryResult> HandleAsync(GetWalletQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var balance = await walletRepo.GetBalanceAsync(tenantId, ct);
        return new WalletQueryResult(balance, options.Currency); // the DTO keeps decimal (titan-ops architecture/money-and-wallet.md#money-is-long-vnd)
    }
}

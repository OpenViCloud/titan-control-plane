using Titan.Application.Commands.Billing.Wallet.Contracts;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using WalletAggregate = Titan.Domain.Aggregates.Wallet.Wallet;

namespace Titan.Application.Commands.Billing.Wallet;

/// Opens the tenant's wallet at activation (called by the provision callback), so every provisioned
/// tenant has one from the start. Not tenant-scoped: the caller passes the tenant being provisioned.
public class OpenWalletCommandHandler(IWalletRepository walletRepo, CreditOptions options)
{
    public async Task HandleAsync(OpenWalletCommand command, CancellationToken ct = default)
    {
        var tenantId = command.TenantId;

        // The provision callback may be retried, and a backfill may race a first top-up: never a second
        // row, never a reset balance.
        if (await walletRepo.GetByTenantForUpdateAsync(tenantId, ct) is not null) return;

        walletRepo.Add(WalletAggregate.Open(tenantId, options.Currency));
        try
        {
            await walletRepo.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // A concurrent open won the UNIQUE(tenant_id) race and inserted the row first. That is the
            // outcome we want — one wallet per tenant — so treat losing the race as success.
        }
    }
}

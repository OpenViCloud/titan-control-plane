using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Repositories;

namespace Titan.Domain.Services;

/// The one way a tenant becomes Active (titan-ops architecture/tenancy-and-admin.md#approval-is-activation): it has a wallet
/// from that moment. The rule spans the tenant and its wallet, so it is a domain service; Tenant.Approve and
/// Activate are internal. The caller loads the tenant with its row locked and saves once after this, so the
/// wallet and the Active tenant commit together; a second activation waits for that commit, and the wallet is read after
/// the lock, so it opens no second one. The tenant refuses first, so a refused activation opens no wallet; the wallet
/// opens empty.
public class TenantActivation(IWalletRepository wallets)
{
    /// An account the realm declares (role auto-approve), approved without an admin.
    public async Task ApproveAsync(Tenant tenant, string currency, CancellationToken ct = default)
    {
        tenant.Approve();
        Wallet? wallet = await wallets.GetByTenantForUpdateAsync(tenant.Id, ct);
        if (wallet == null)
        {
            wallets.Add(Wallet.Open(tenant.Id, currency));
        }
    }

    /// The admin's Active: a pending, rejected or suspended tenant. One coming back keeps the wallet it had.
    public async Task ActivateAsync(Tenant tenant, string currency, CancellationToken ct = default)
    {
        tenant.Activate();
        Wallet? wallet = await wallets.GetByTenantForUpdateAsync(tenant.Id, ct);
        if (wallet == null)
        {
            wallets.Add(Wallet.Open(tenant.Id, currency));
        }
    }
}

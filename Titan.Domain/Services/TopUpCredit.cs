using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Domain.Services;

/// The one way a top-up becomes money: a confirmed one has exactly one credit, a promotional one its ledger row. The rule
/// spans the top-up, the wallet and the credit, so it is a domain service; TopUp.Confirm and GrantPromo are internal.
/// Everything commits in the caller's save.
public class TopUpCredit(
    IWalletRepository wallets, IWalletMutationRepository mutations, ICreditRepository topUps, WalletLedger ledger)
{
    /// Money is in, and the credit is enqueued, not applied (titan-ops architecture/money-and-wallet.md#single-writer-outbox):
    /// crediting here would let two confirms of one top-up race on the balance. The mutation commits with the Confirmed
    /// status, and UNIQUE(ref_type, ref_id) stops a second confirm from enqueueing a second credit. The wallet is only read
    /// to name it in the mutation. confirmedAt lets the SePay webhook stamp the payment's own transactionDate; null means now.
    public async Task<WalletMutation> ConfirmAsync(
        TopUp topUp, TopUpSource source, string confirmedBy, string? paymentTxnRef, DateTimeOffset? confirmedAt,
        CancellationToken ct = default)
    {
        topUp.Confirm(source, confirmedBy, paymentTxnRef, confirmedAt);
        Wallet wallet = await OpenedWalletAsync(topUp.TenantId, ct);
        WalletMutation credit = WalletMutation.Create(
            WalletMutationType.Credit, wallet.Id, topUp.Amount, WalletMutation.TopUpRefType, topUp.Id);
        mutations.Add(credit);
        return credit;
    }

    /// The grant row and the money commit in one save. A Promo ledger movement keeps free credit separable from real
    /// money (titan-ops architecture/money-and-wallet.md#tax-legal-posture).
    public async Task<TopUp> GrantPromoAsync(
        Guid tenantId, long amount, string currency, string? reason, string grantedBy, CancellationToken ct = default)
    {
        TopUp topUp = TopUp.GrantPromo(tenantId, amount, currency, reason, grantedBy);
        Wallet wallet = await OpenedWalletAsync(tenantId, ct);
        ledger.Credit(wallet, WalletTransactionType.Promo, amount, topUp.Id);
        topUps.AddTopUp(topUp);
        return topUp;
    }

    private async Task<Wallet> OpenedWalletAsync(Guid tenantId, CancellationToken ct)
    {
        Wallet? wallet = await wallets.GetByTenantForUpdateAsync(tenantId, ct);
        if (wallet == null)
        {
            throw new NotFoundException("WALLET_NOT_FOUND",
                $"Tenant {tenantId} has no wallet; it should have been opened at activation.");
        }
        return wallet;
    }
}

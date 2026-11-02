using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Application.Services.Billing.WalletMutations;

// The single-writer consumer (titan-ops architecture/money-and-wallet.md#single-writer-outbox): the subscriber and the
// sweeper both call it, and the rule itself is WalletMutationApply; the wallet version token catches two applies that
// read the wallet at the same time.
//
// A non-Pending mutation is a no-op, so a redelivered notify is safe. The check reads the mutation this
// context loaded, so it only sees another context's apply when the context is fresh: every caller gives each
// apply its own scope (titan-ops features/billing/wallet/design.md#wallet-mutation-flow).
public interface IWalletMutationConsumer
{
    /// Commits the balance change, the ledger row and the mutation's verdict in one transaction
    /// (titan-ops architecture/money-and-wallet.md#wallet-invariant-in-aggregate).
    Task ApplyAsync(Guid mutationId, CancellationToken ct = default);
}

public class WalletMutationConsumer(
    IWalletMutationRepository mutationRepo,
    IWalletRepository walletRepo,
    WalletMutationApply apply) : IWalletMutationConsumer
{
    public async Task ApplyAsync(Guid mutationId, CancellationToken ct = default)
    {
        var mutation = await mutationRepo.GetForUpdateAsync(mutationId, ct);
        if (mutation is null) return;                  // a notify for a mutation that never landed

        // Only a Pending mutation has work left: a redelivered notify returns here without touching the wallet.
        if (mutation.Status != WalletMutationStatus.Pending) return;

        await apply.ApplyAsync(mutation, ct);

        // Every repository behind apply.ApplyAsync, the ledger's too, shares the DbContext, so one SaveChanges commits balance, ledger row and status.
        // A double-apply that slipped past the status check fails here on the ledger UNIQUE(type, ref_id) or the
        // version token; the failed save does not undo the balance change, so the context must not be reused.
        await walletRepo.SaveChangesAsync(ct);
    }
}

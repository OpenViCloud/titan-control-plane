using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Billing.TopUp;

// An admin (ConfirmTopUpCommandHandler) and the SePay webhook (ReceiveSePayPaymentCommandHandler) both
// confirm a TopUp, and handlers may not call each other
// (ReadWriteSeparationTests.Handlers_DoNotReferenceOtherHandlers), so the rule lives here
// (titan-ops architecture/code-structure.md#services-branch).
public static class TopUpConfirmation
{
    /// Money is in. The credit is enqueued, not applied (titan-ops architecture/money-and-wallet.md#single-writer-outbox).
    /// Not tenant-scoped: an admin or the SePay webhook calls it.
    public static async Task ConfirmAsync(
        ICreditRepository repo,
        IWalletRepository walletRepo,
        IWalletMutationRepository mutationRepo,
        IWalletMutationPublisher walletBus,
        Guid topUpId, TopUpSource source, string confirmedBy, string? paymentTxnRef,
        DateTimeOffset? confirmedAt, CancellationToken ct)
    {
        var topUp = await repo.GetTopUpForUpdateAsync(topUpId, ct)
            ?? throw new NotFoundException("TOPUP_NOT_FOUND", $"Top-up {topUpId} not found.");

        // A retry of an already-confirmed top-up credits nothing more: double webhook, double click.
        if (topUp.Status == TopUpStatus.Confirmed) return;

        // Throws TOPUP_INVALID_STATE for a Failed top-up. confirmedAt lets the SePay webhook stamp the
        // payment's own transactionDate; null means now. topUp is tracked, so the SaveChanges below persists it.
        topUp.Confirm(source, confirmedBy, paymentTxnRef, confirmedAt);

        // The credit is deferred to the single-writer consumer (titan-ops architecture/money-and-wallet.md#single-writer-outbox):
        // crediting here would let two confirms of one top-up race on the balance. The mutation commits
        // with the Confirmed status, and UNIQUE(ref_type, ref_id) stops a second confirm from enqueueing a
        // second credit. The wallet is only read to name it in the mutation.
        var wallet = await walletRepo.GetByTenantForUpdateAsync(topUp.TenantId, ct)
            ?? throw new NotFoundException("WALLET_NOT_FOUND",
                $"Tenant {topUp.TenantId} has no wallet; it should have been opened at activation.");
        var mutation = WalletMutation.ForCredit(wallet.Id, topUp.Amount, "TopUp", topUp.Id);
        mutationRepo.Add(mutation);

        try
        {
            // walletRepo.SaveChanges commits the shared context — the Confirmed status AND the mutation —
            // and translates the mutation's UNIQUE(ref_type, ref_id) violation into ConcurrencyConflictException.
            await walletRepo.SaveChangesAsync(ct);
        }
        catch (ConcurrencyConflictException)
        {
            // A concurrent confirm of this top-up won the unique guard, so answer as the winner's
            // confirmation. Anything else is a rare cross-operation race, surfaced for retry.
            var winner = await repo.GetTopUpAsync(topUpId, ct);
            if (winner is { Status: TopUpStatus.Confirmed }) return;
            throw new ConflictException("TOPUP_CONFIRM_CONFLICT",
                $"Top-up {topUpId} hit a concurrent confirm and was not recorded; retry.");
        }

        // Post-commit notify so the credit is prompt. The mutation is already durable and the sweeper
        // re-drives Pending ones, so a bus error must not fail a confirm the customer's money backs.
        try
        {
            await walletBus.PublishAsync(new WalletMutationNotification(mutation.Id, wallet.Id), ct);
        }
        catch
        {
            // Swallowed on purpose — see above.
        }
    }
}

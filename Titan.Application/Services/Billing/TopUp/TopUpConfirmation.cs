using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Application.Services.Billing.TopUp;

// An admin (ConfirmTopUpCommandHandler) and the SePay webhook (ReceiveSePayPaymentCommandHandler) both confirm a TopUp,
// and handlers may not call each other (ReadWriteSeparationTests.Handlers_DoNotReferenceOtherHandlers), so what surrounds
// the rule lives here: the repeat, the save, the notify. The rule is TopUpCredit.
public static class TopUpConfirmation
{
    /// Not tenant-scoped: an admin or the SePay webhook calls it.
    public static async Task ConfirmAsync(
        ITopUpRepository repo,
        IWalletRepository walletRepo,
        TopUpCredit topUpCredit,
        IWalletMutationPublisher walletBus,
        Guid topUpId, TopUpSource source, string confirmedBy, string? paymentTxnRef,
        DateTimeOffset? confirmedAt, CancellationToken ct)
    {
        var topUp = await repo.GetTopUpForUpdateAsync(topUpId, ct)
            ?? throw new NotFoundException("TOPUP_NOT_FOUND", $"Top-up {topUpId} not found.");

        // A retry of an already-confirmed top-up credits nothing more: double webhook, double click.
        if (topUp.Status == TopUpStatus.Confirmed) return;

        // topUp is tracked, so the SaveChanges below persists it.
        var mutation = await topUpCredit.ConfirmAsync(topUp, source, confirmedBy, paymentTxnRef, confirmedAt, ct);

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
            await walletBus.PublishAsync(new WalletMutationNotification(mutation.Id, mutation.WalletId), ct);
        }
        catch
        {
            // Swallowed on purpose — see above.
        }
    }
}

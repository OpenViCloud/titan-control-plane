using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.TopUp;
using Titan.Application.Commands.Billing.PaymentGatewayTransaction.Contracts;
using Titan.Domain.Aggregates.PaymentGatewayTransaction;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using TopUpAggregate = Titan.Domain.Aggregates.TopUp.TopUp;
using PaymentGatewayTransactionAggregate =
    Titan.Domain.Aggregates.PaymentGatewayTransaction.PaymentGatewayTransaction;

namespace Titan.Application.Commands.Billing.PaymentGatewayTransaction;

/// Turns an inbound SePay payment into a confirmed TopUp or a recorded anomaly. Every path is idempotent
/// so SePay's retries (up to 7×) never double-credit: dedupe by SePay txn id, and UNIQUE(ref_type, ref_id)
/// on the credit mutation. Not tenant-scoped: the webhook attributes by PaymentRef.
public class ReceiveSePayPaymentCommandHandler(
    IPaymentGatewayTransactionRepository payments,
    ITopUpRepository credit,
    IWalletRepository walletRepo,
    TopUpCredit topUpCredit,
    IWalletMutationPublisher walletBus,
    ILogger<ReceiveSePayPaymentCommandHandler> logger)
{
    // SePay's word for money that came in.
    private const string Inbound = "in";

    public async Task<SePayWebhookOutcome> HandleAsync(ReceiveSePayPaymentCommand p, CancellationToken ct = default)
    {
        // Only inbound money adds credit; an outbound transfer is ignored, not recorded.
        if (!string.Equals(p.TransferType, Inbound, StringComparison.OrdinalIgnoreCase))
            return SePayWebhookOutcome.Ignored;

        // A retried webhook (same SePay txn id) is a no-op. The DB is the idempotency store, so it
        // survives a restart between retries.
        if (await payments.ExistsAsync(p.Id, ct))
            return SePayWebhookOutcome.Duplicate;

        var paymentRef = TopUpAggregate.FindPaymentRef(p.Code, p.Content);
        var topUp = paymentRef is null ? null : await credit.GetTopUpByPaymentRefAsync(paymentRef, ct);

        if (topUp is null)
        {
            // Money arrived Titan cannot attribute — record + alert-ready log, do NOT ask SePay to retry
            // (retry cannot fix a wrong ref). An admin reconciles the orphan by hand.
            logger.LogWarning(
                "SePay ORPHAN payment: txn {TxnId} amount {Amount} content {Content} matched no TopUp",
                p.Id, p.TransferAmount, p.Content);
            await RecordAsync(p, PaymentGatewayTransactionStatus.Orphan, null, null, ct);
            return SePayWebhookOutcome.Orphan;
        }

        if (topUp.IsConfirmed)
        {
            // The TopUp was already confirmed (e.g. an admin recorded it first) — no re-credit.
            await RecordAsync(p, PaymentGatewayTransactionStatus.Duplicate, topUp.TenantId, topUp.Id, ct);
            return SePayWebhookOutcome.Duplicate;
        }

        if (!topUp.IsPaidBy(p.TransferAmount))
        {
            // The amount does not match the purchase — do not credit; record + alert for reconciliation.
            logger.LogWarning(
                "SePay AMOUNT MISMATCH: txn {TxnId} paid {Paid} but TopUp {TopUpId} expects {Expected}",
                p.Id, p.TransferAmount, topUp.Id, topUp.Amount);
            await RecordAsync(p, PaymentGatewayTransactionStatus.AmountMismatch, topUp.TenantId, topUp.Id, ct);
            return SePayWebhookOutcome.AmountMismatch;
        }

        // Stamped at the payment's own transactionDate, not the webhook arrival time.
        await TopUpConfirmation.ConfirmAsync(
            credit, walletRepo, topUpCredit, walletBus,
            topUp.Id, TopUpSource.SePay, $"sepay:{p.Id}", p.Id, p.TransactionDate, ct);
        await RecordAsync(p, PaymentGatewayTransactionStatus.Matched, topUp.TenantId, topUp.Id, ct);
        return SePayWebhookOutcome.Matched;
    }

    private Task RecordAsync(
        ReceiveSePayPaymentCommand p, PaymentGatewayTransactionStatus status, Guid? tenantId, Guid? topUpId, CancellationToken ct) =>
        payments.TryAddAsync(
            PaymentGatewayTransactionAggregate.Record(
                p.Id, status, (long)p.TransferAmount, p.Gateway, p.TransactionDate, p.Content, tenantId, topUpId),
            ct);
}

using FastEndpoints;
using Microsoft.Extensions.Logging;
using Titan.Application.Options;
using Titan.Application.Commands.Billing.PaymentGatewayTransaction;
using Titan.Application.Commands.Billing.PaymentGatewayTransaction.Contracts;

namespace Titan.Infrastructure.Endpoints.Payments;

// Real money landed in the merchant bank account. Authenticated by the SePay Apikey, not the JWT or
// X-Internal-Token. Answers 200 {"success":true} once authenticated so SePay stops retrying (up to 7×):
// orphan/mismatch cases are caught by the recorded PaymentGatewayTransaction, not by bouncing SePay.
public record SePayWebhookRequest(
    long? Id, string? Gateway, string? TransactionDate, string? TransferType,
    decimal? TransferAmount, string? Content, string? Code);

public class SePayWebhookEndpoint(
    ReceiveSePayPaymentCommandHandler handler, SePayOptions options, ILogger<SePayWebhookEndpoint> logger)
    : Endpoint<SePayWebhookRequest>
{
    public override void Configure()
    {
        Post("internal/payments/sepay/webhook");
        AllowAnonymous(); // authenticated by the SePay Apikey below, not the JWT
    }

    public override async Task HandleAsync(SePayWebhookRequest req, CancellationToken ct)
    {
        var auth = HttpContext.Request.Headers.Authorization.FirstOrDefault();
        if (!SePayApiKeyAuth.Matches(auth, options.ApiKey))
        {
            // Do not leak whether a matching payment/invoice exists: just refuse.
            await SendUnauthorizedAsync(ct);
            return;
        }

        // A malformed body is a controlled 400, never a silent 500.
        if (req.Id is null || string.IsNullOrEmpty(req.TransferType) || req.TransferAmount is null
            || !TryParseTransactionDate(req.TransactionDate, out var txnDate))
        {
            logger.LogWarning("SePay webhook malformed body: {@Body}", req);
            await SendAsync(new { success = false, error = "malformed" }, 400, ct);
            return;
        }

        var payload = new ReceiveSePayPaymentCommand(
            req.Id.Value.ToString(), req.Gateway ?? "unknown", txnDate,
            req.TransferType!, req.TransferAmount.Value, req.Content, req.Code);

        var outcome = await handler.HandleAsync(payload, ct);
        logger.LogInformation("SePay webhook txn {TxnId} → {Outcome}", payload.Id, outcome);

        // 200 {"success":true} regardless of outcome so SePay does not retry (retry cannot fix an
        // orphan/mismatch — those are reconciled by an admin from the PaymentGatewayTransaction ledger).
        await SendOkAsync(new { success = true }, ct);
    }

    // SePay sends transactionDate like "2026-08-27 14:02:37" (no cluster). Treat as UTC; accept ISO too.
    private static bool TryParseTransactionDate(string? s, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        return DateTimeOffset.TryParse(
            s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out value);
    }
}

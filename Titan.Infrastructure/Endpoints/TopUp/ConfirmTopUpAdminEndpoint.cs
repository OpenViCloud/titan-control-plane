using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Billing.TopUp;
using Titan.Application.Commands.Billing.TopUp.Contracts;

namespace Titan.Infrastructure.Endpoints.TopUp;

// The manual fallback for a real-money top-up an admin reconciled by hand.
//
// 202: the credit is queued in the outbox, not applied, when this returns
// (titan-ops architecture/http-contract.md#async-actions-return-202); the confirmed TopUp is read back from
// GET /v1/admin/topups (titan-ops architecture/money-and-wallet.md#confirmed-topup-is-receipt).
public class ConfirmTopUpAdminEndpoint(ConfirmTopUpCommandHandler handler) : Endpoint<ConfirmTopUpBody>
{
    public override void Configure()
    {
        Post("v1/admin/topups/{id:guid}/confirm");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(ConfirmTopUpBody body, CancellationToken ct)
    {
        var id = Route<Guid>("id");
        await handler.HandleAsync(new ConfirmTopUpCommand(id, body.PaymentTxnRef), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted));
    }
}

// The JSON body alone; ConfirmTopUpCommand additionally carries the route id (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record ConfirmTopUpBody(string? PaymentTxnRef);

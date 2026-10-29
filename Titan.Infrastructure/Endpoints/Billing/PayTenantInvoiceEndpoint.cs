using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;

namespace Titan.Infrastructure.Endpoints.Billing;

/// titan-ops architecture/money-and-wallet.md#human-confirmed-debit. No body: an amount would invite a partial payment,
/// and a payment is all or nothing.
///
/// 202, because the money has not moved yet: the consumer applies the debit mutation later
/// (titan-ops architecture/http-contract.md#async-actions-return-202). A rejection the request can answer (nothing
/// owing, not enough money) is still a 4xx, before any mutation is written.
public class PayTenantInvoiceEndpoint(PayInvoiceFromWalletCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("v1/tenant/billing/invoices/{invoiceId:guid}/pay");
        Description(b => b.ClearDefaultProduces(200).Produces(202));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var invoiceId = Route<Guid>("invoiceId");
        await handler.HandleAsync(new PayInvoiceFromWalletCommand(invoiceId), ct);
        await SendResultAsync(TypedResults.StatusCode(StatusCodes.Status202Accepted));
    }
}

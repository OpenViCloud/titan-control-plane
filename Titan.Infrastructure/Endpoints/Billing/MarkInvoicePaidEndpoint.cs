using FastEndpoints;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;

namespace Titan.Infrastructure.Endpoints.Billing;

// Money that arrived outside Titan: never touches the wallet, so PaymentState stays null
// (titan-ops architecture/money-and-wallet.md#human-confirmed-debit).
public class MarkInvoicePaidEndpoint(MarkInvoicePaidCommandHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Patch("v1/admin/invoices/{invoiceId:guid}/pay");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var invoiceId = Route<Guid>("invoiceId");
        await handler.HandleAsync(new MarkInvoicePaidCommand(invoiceId), ct);
        await SendNoContentAsync(ct);
    }
}

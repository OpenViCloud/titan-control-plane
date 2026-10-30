using Titan.Application.Services.Billing.Invoice;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.Invoice;

// Admin view, across tenants — no ICurrentUser: the endpoint is platform-admin only.
public class ListInvoicesQueryHandler(IInvoiceRepository invoiceRepo, IWalletMutationRepository mutationRepo)
{
    public async Task<IReadOnlyList<InvoiceQueryResult>> HandleAsync(ListInvoicesQuery query, CancellationToken ct = default)
    {
        var status = query.Status;
        var invoices = await invoiceRepo.ListAllAsync(ct);
        var now = DateTimeOffset.UtcNow;

        // Under an async debit, Status alone cannot tell "nobody paid this" from "payment in flight" or
        // "payment failed", and this is the view an admin chasing receivables acts on.
        var states = await InvoicePaymentStates.LookUpAsync(mutationRepo, invoices.Select(i => i.Id).ToList(), ct);

        var dtos = invoices.OrderByDescending(i => i.IssuedAt)
            .Select(i => InvoiceQueryResult.From(i, now, states.GetValueOrDefault(i.Id)));

        // Filters on the derived DisplayStatus (e.g. "Overdue"), which is never a stored column:
        // filtering on Status directly would silently miss every overdue invoice.
        if (!string.IsNullOrWhiteSpace(status))
            dtos = dtos.Where(d => string.Equals(d.Status.ToString(), status, StringComparison.OrdinalIgnoreCase));

        return dtos.ToList();
    }
}

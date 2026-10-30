using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.Invoice;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.Invoice;

public class ListTenantInvoicesQueryHandler(
    IInvoiceRepository invoiceRepo, IWalletMutationRepository mutationRepo, ICurrentUser currentUser)
{
    public async Task<IReadOnlyList<InvoiceQueryResult>> HandleAsync(
        ListTenantInvoicesQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var invoices = await invoiceRepo.ListByTenantAsync(tenantId, ct);
        var now = DateTimeOffset.UtcNow;

        // One query for every invoice's payment state: a per-row lookup inside the projection would run
        // concurrent queries on one scoped DbContext, which EF refuses.
        var states = await InvoicePaymentStates.LookUpAsync(mutationRepo, invoices.Select(i => i.Id).ToList(), ct);

        return invoices
            .OrderByDescending(i => i.IssuedAt)
            .Select(i => InvoiceQueryResult.From(i, now, states.GetValueOrDefault(i.Id)))
            .ToList();
    }
}

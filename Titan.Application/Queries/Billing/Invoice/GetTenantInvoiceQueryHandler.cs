using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.Invoice;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.Invoice;

public class GetTenantInvoiceQueryHandler(
    IInvoiceRepository invoiceRepo, IWalletMutationRepository mutationRepo, ICurrentUser currentUser)
{
    public async Task<InvoiceDetailQueryResult> HandleAsync(GetTenantInvoiceQuery query, CancellationToken ct = default)
    {
        var id = query.InvoiceId;
        var tenantId = currentUser.RequireTenantId();
        var invoice = await invoiceRepo.GetByIdAsync(id, ct);

        // Same error for "does not exist" and "belongs to another tenant" — telling those apart
        // would confirm to the caller that another tenant's invoice id exists.
        if (invoice is null || invoice.TenantId != tenantId)
            throw new NotFoundException("INVOICE_NOT_FOUND", $"Invoice {id} not found.");

        var mutation = await mutationRepo.GetForRefAsync(
            WalletMutationConsumer.InvoiceRefType, invoice.Id, ct);

        return InvoiceDetailQueryResult.From(
            invoice, DateTimeOffset.UtcNow, mutation is null ? null : InvoicePaymentStates.From(mutation.Status));
    }
}

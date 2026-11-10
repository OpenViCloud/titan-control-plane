using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.Invoice;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.Invoice;

public class GetTenantInvoiceQueryHandler(
    IInvoiceRepository invoiceRepo, IWalletMutationRepository mutationRepo, ICurrentUser currentUser)
{
    public async Task<InvoiceDetailQueryResult> HandleAsync(GetTenantInvoiceQuery query, CancellationToken ct = default)
    {
        var invoice = await InvoiceOwnership.RequireInvoiceAsync(invoiceRepo, currentUser.RequireTenantId(), query.InvoiceId, ct);

        var mutation = await mutationRepo.GetForRefAsync(
            WalletMutation.InvoiceRefType, invoice.Id, ct);

        return InvoiceDetailQueryResult.From(
            invoice, DateTimeOffset.UtcNow, mutation is null ? null : InvoicePaymentStates.From(mutation.Status));
    }
}

using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using InvoiceAggregate = Titan.Domain.Aggregates.Invoice.Invoice;

namespace Titan.Application.Services.Billing.Invoice;

/// A tenant's invoice, or 404: the same answer for "does not exist" and "belongs to another tenant", so a read or a
/// payment cannot probe for invoice ids (titan-ops architecture/http-contract.md#tenant-scoping).
public static class InvoiceOwnership
{
    public static async Task<InvoiceAggregate> RequireInvoiceAsync(
        IInvoiceRepository invoiceRepo, Guid tenantId, Guid invoiceId, CancellationToken ct) =>
        Owned(await invoiceRepo.GetByIdAsync(invoiceId, ct), tenantId, invoiceId);

    /// The same answer with the invoice's row taken for update (IInvoiceRepository.GetForUpdateAsync): a payment.
    public static async Task<InvoiceAggregate> RequireInvoiceForUpdateAsync(
        IInvoiceRepository invoiceRepo, Guid tenantId, Guid invoiceId, CancellationToken ct) =>
        Owned(await invoiceRepo.GetForUpdateAsync(invoiceId, ct), tenantId, invoiceId);

    private static InvoiceAggregate Owned(InvoiceAggregate? invoice, Guid tenantId, Guid invoiceId)
    {
        if (invoice is null || invoice.TenantId != tenantId)
        {
            throw new NotFoundException("INVOICE_NOT_FOUND", $"Invoice {invoiceId} not found.");
        }
        return invoice;
    }
}

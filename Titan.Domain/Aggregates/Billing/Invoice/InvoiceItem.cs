using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Billing.Invoice;

/// One resource for the billed period. Amount is frozen at generation and never recomputed
/// (titan-ops architecture/money-and-wallet.md#invoice-frozen-snapshot). Every line is usage: wallet credit is spent
/// when a person pays (titan-ops architecture/money-and-wallet.md#human-confirmed-debit), not netted into the invoice.
public class InvoiceItem : ChildEntity
{
    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public UsageResourceType ResourceType { get; private set; }
    public Guid ResourceId { get; private set; }
    public string ResourceName { get; private set; } = default!; // snapshot from UsageRecord, not the vms/volumes table
    /// Whole VND: the caller rounds the raw sum of a line's usage records once (UsageRecord.TotalVnd).
    public long Amount { get; private set; }
    public string Currency { get; private set; } = default!;

    private InvoiceItem() { }

    public static InvoiceItem Create(
        UsageResourceType resourceType, Guid resourceId, string resourceName, long amount, string currency)
    {
        // Usage is never negative; zero is a line for a resource that cost nothing this period.
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "An invoice line cannot be negative.");
        }

        return new InvoiceItem
        {
            Id = Guid.NewGuid(),
            // The item is built before its Invoice exists; Invoice.Generate stamps the id via AttachToInvoice.
            InvoiceId = Guid.Empty,
            ResourceType = resourceType,
            ResourceId = resourceId,
            ResourceName = resourceName,
            Amount = amount,
            Currency = currency
        };
    }

    /// Called only by Invoice.Generate, once the owning invoice's id exists.
    internal void AttachToInvoice(Guid invoiceId) => InvoiceId = invoiceId;
}

using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Billing.Invoice;

/// A frozen snapshot of one month's UsageRecords (titan-ops architecture/money-and-wallet.md#invoice-frozen-snapshot):
/// after Generate only the single payment transition changes it; Total and every item Amount are fixed.
public class Invoice : AggregateRoot
{
    /// DueAt = IssuedAt + PaymentTermDays (titan-ops features/billing/invoice/design.md#invoice-generation).
    public const int PaymentTermDays = 7;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public int PeriodYear { get; private set; }
    public int PeriodMonth { get; private set; }
    /// A concurrency token: the double-pay guard (titan-ops architecture/money-and-wallet.md#double-pay-guard).
    public InvoiceStatus Status { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public string? PaidBy { get; private set; }
    /// Whole VND, summed from the items. Sum over an empty sequence is 0, and a zero total settles itself
    /// (AutoSettleZeroTotal).
    public long Total { get; private set; }
    public string Currency { get; private set; } = default!;

    private readonly List<InvoiceItem> _items = [];
    public IReadOnlyList<InvoiceItem> Items => _items;

    private Invoice() { }

    /// Items arrive grouped, one per resource. Total is computed here so it can never drift from the items.
    public static Invoice Generate(
        Guid tenantId, int year, int month, DateTimeOffset issuedAt,
        IReadOnlyList<InvoiceItem> items, string currency)
    {
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            PeriodYear = year,
            PeriodMonth = month,
            Status = InvoiceStatus.New,
            IssuedAt = issuedAt,
            DueAt = issuedAt.AddDays(PaymentTermDays),
            Total = items.Sum(i => i.Amount),
            Currency = currency
        };

        foreach (var item in items)
        {
            item.AttachToInvoice(invoice.Id);
            invoice._items.Add(item);
        }

        return invoice;
    }

    /// Money that arrived outside Titan, recorded by a platform admin. Throws on a settled invoice: the caller
    /// needs to know it asked to pay something already paid.
    public void MarkPaid(DateTimeOffset paidAt, string paidBy)
    {
        // Recording a real payment on a wallet-settled invoice would be a double settlement.
        if (Status is InvoiceStatus.Paid or InvoiceStatus.SettledByWallet)
            throw new BusinessException("INVOICE_ALREADY_PAID", $"Invoice {Id} is already settled.");

        Status = InvoiceStatus.Paid;
        PaidAt = paidAt;
        PaidBy = paidBy;
    }

    /// Not an exception to titan-ops architecture/money-and-wallet.md#human-confirmed-debit: at zero no money moves, so
    /// there is nothing to confirm, and a 0 ₫ bill left New would age to Overdue. Paid, not SettledByWallet:
    /// the wallet is untouched, and claiming otherwise would put a phantom spend in the statement.
    public void AutoSettleZeroTotal()
    {
        if (Total != 0)
            throw new BusinessException("INVOICE_INVALID_STATE",
                $"Only a zero-total invoice settles itself (was {Total} {Currency}).");
        if (Status != InvoiceStatus.New)
            throw new BusinessException("INVOICE_INVALID_STATE",
                $"Only a new invoice can auto-settle (was {Status}).");

        Status = InvoiceStatus.Paid;
        PaidAt = IssuedAt;
        PaidBy = "system";
    }

    /// A person pressed pay (titan-ops architecture/money-and-wallet.md#human-confirmed-debit). From New only; being past
    /// DueAt does not matter — overdue only reminds (titan-ops architecture/money-and-wallet.md#postpaid-no-brake).
    public void SettleFromWallet(DateTimeOffset paidAt)
    {
        if (Status != InvoiceStatus.New)
            throw new BusinessException("INVOICE_INVALID_STATE",
                $"Only an outstanding invoice can be settled from the wallet (was {Status}).");

        Status = InvoiceStatus.SettledByWallet;
        PaidAt = paidAt;
        PaidBy = "wallet";
    }

    /// Overdue is never stored: it is "New, past DueAt" against the clock at read time. A settled invoice is
    /// never Overdue, however late it was paid.
    public InvoiceDisplayStatus DisplayStatus(DateTimeOffset now)
    {
        if (Status == InvoiceStatus.Paid) return InvoiceDisplayStatus.Paid;
        if (Status == InvoiceStatus.SettledByWallet) return InvoiceDisplayStatus.Settled;
        return now > DueAt ? InvoiceDisplayStatus.Overdue : InvoiceDisplayStatus.New;
    }
}

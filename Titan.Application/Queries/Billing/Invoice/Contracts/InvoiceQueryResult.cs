using System.Text.Json.Serialization;
using InvoiceAggregate = Titan.Domain.Aggregates.Invoice.Invoice;
using Titan.Domain.Aggregates.Invoice;

namespace Titan.Application.Queries.Billing.Invoice.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse).
public record InvoiceQueryResult(
    Guid Id, Guid TenantId, int PeriodYear, int PeriodMonth, [property: JsonConverter(typeof(JsonStringEnumConverter))] InvoiceDisplayStatus Status,
    DateTimeOffset IssuedAt, DateTimeOffset DueAt, DateTimeOffset? PaidAt, string? PaidBy,
    decimal Total, string Currency, string? PaymentState = null)
{
    public static InvoiceQueryResult From(InvoiceAggregate invoice, DateTimeOffset now, PaymentState? paymentState = null) =>
        new(invoice.Id, invoice.TenantId, invoice.PeriodYear, invoice.PeriodMonth,
            // The wire keeps the string label; the display enum is a C#-side detail.
            invoice.DisplayStatus(now), invoice.IssuedAt, invoice.DueAt, invoice.PaidAt, invoice.PaidBy,
            invoice.Total, invoice.Currency,
            // Same house convention as Status: the enum is a C#-side detail, the wire carries its name.
            paymentState?.ToString());
}

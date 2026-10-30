using System.Text.Json.Serialization;
using InvoiceAggregate = Titan.Domain.Aggregates.Invoice.Invoice;
using Titan.Domain.Aggregates.Invoice;

namespace Titan.Application.Queries.Billing.Invoice.Contracts;

public record InvoiceDetailQueryResult(
    Guid Id, Guid TenantId, int PeriodYear, int PeriodMonth, [property: JsonConverter(typeof(JsonStringEnumConverter))] InvoiceDisplayStatus Status,
    DateTimeOffset IssuedAt, DateTimeOffset DueAt, DateTimeOffset? PaidAt, string? PaidBy,
    decimal Total, string Currency, IReadOnlyList<InvoiceItemLine> Items, string? PaymentState = null)
{
    public static InvoiceDetailQueryResult From(InvoiceAggregate invoice, DateTimeOffset now, PaymentState? paymentState = null) =>
        new(invoice.Id, invoice.TenantId, invoice.PeriodYear, invoice.PeriodMonth,
            invoice.DisplayStatus(now), invoice.IssuedAt, invoice.DueAt, invoice.PaidAt, invoice.PaidBy,
            invoice.Total, invoice.Currency, invoice.Items.Select(InvoiceItemLine.From).ToList(),
            paymentState?.ToString());
}

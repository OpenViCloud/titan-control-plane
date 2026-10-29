namespace Titan.Application.Queries.Billing.Invoice.Contracts;

/// DebtCeiling/OverCeiling let the admin closing invoices at month-end see, on one line, what a tenant
/// owes and what it was expected to owe. Nothing acts on OverCeiling
/// (titan-ops architecture/money-and-wallet.md#postpaid-no-brake): it is a number for a person to read.
///
/// DebtCeiling is 0 for a tenant whose row is gone (same fallback as TenantName → guid): the report
/// must still show the debt rather than 500 an ops question.
public record TenantReceivableLine(
    Guid TenantId,
    string TenantName,
    decimal Outstanding,
    int InvoiceCount,
    decimal OverdueAmount,
    int OverdueCount,
    DateTimeOffset OldestDueAt,
    decimal DebtCeiling,
    bool OverCeiling);

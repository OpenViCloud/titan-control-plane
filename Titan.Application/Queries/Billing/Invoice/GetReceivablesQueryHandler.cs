using Titan.Application.Services.Billing;
using Titan.Application.Queries.Billing.Invoice.Contracts;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.Invoice;

/// Accounts receivable for admin (titan-ops architecture/money-and-wallet.md#postpaid-no-brake). `Overdue` is
/// derived at read time and stored nowhere, so nothing can alert on it directly; this is how a postpaid
/// platform measures what it is owed.
public class GetReceivablesQueryHandler(IInvoiceRepository invoiceRepo, ITenantRepository tenantRepo)
{
    // The same constant the meters stamp.
    private const string DefaultCurrency = BillingConstants.DefaultCurrency;

    public async Task<ReceivablesQueryResult> HandleAsync(GetReceivablesQuery query, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var outstanding = await invoiceRepo.ListOutstandingAllAsync(ct);

        // One tenant read, then an in-memory lookup: a per-invoice lookup inside the projection would run
        // concurrent queries on one scoped DbContext, which EF refuses.
        var tenants = (await tenantRepo.ListAllAsync(ct))
            .ToDictionary(t => t.Id, t => (t.Name, t.DebtCeiling));

        var perTenant = outstanding
            .GroupBy(i => i.TenantId)
            .Select(g =>
            {
                var known = tenants.TryGetValue(g.Key, out var t);
                var outstanding = g.Sum(i => i.Total);
                var ceiling = known ? t.DebtCeiling : 0L;
                return new TenantReceivableLine(
                    TenantId: g.Key,
                    TenantName: known ? t.Name : g.Key.ToString(),
                    Outstanding: outstanding,
                    InvoiceCount: g.Count(),
                    OverdueAmount: g.Where(i => i.DisplayStatus(now) == InvoiceDisplayStatus.Overdue).Sum(i => i.Total),
                    OverdueCount: g.Count(i => i.DisplayStatus(now) == InvoiceDisplayStatus.Overdue),
                    OldestDueAt: g.Min(i => i.DueAt),
                    DebtCeiling: ceiling,
                    // Strictly above: a tenant sitting exactly ON its ceiling has not exceeded it.
                    OverCeiling: known && outstanding > ceiling);
            })
            .OrderByDescending(r => r.Outstanding)
            .ToList();

        return new ReceivablesQueryResult(
            TotalOutstanding: perTenant.Sum(r => r.Outstanding),
            TotalOverdue: perTenant.Sum(r => r.OverdueAmount),
            OverdueInvoiceCount: perTenant.Sum(r => r.OverdueCount),
            TenantsInDebt: perTenant.Count,
            Currency: DefaultCurrency,
            PerTenant: perTenant);
    }
}

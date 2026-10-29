using Titan.Application.Services.Billing;
using Titan.Application.Commands.Billing.Invoice.Contracts;
using Titan.Domain.Aggregates.Billing.Invoice;
using Titan.Domain.Repositories.Billing;
using InvoiceAggregate = Titan.Domain.Aggregates.Billing.Invoice.Invoice;
using UsageRecordAggregate = Titan.Domain.Aggregates.Billing.UsageRecord.UsageRecord;

namespace Titan.Application.Commands.Billing.Invoice;

/// An Invoice is a frozen snapshot of the UsageRecord ledger, derived once per (tenant, month) and never
/// recomputed (titan-ops architecture/money-and-wallet.md#invoice-frozen-snapshot). The only creator of Invoice rows.
/// Run once a month after the period has closed; safe to re-run.
public class GenerateInvoicesForPeriodCommandHandler(IInvoiceRepository invoiceRepo, IUsageRecordRepository usageRepo)
{
    // Not read back off the usage rows it bills: every meter stamps rows with this same constant, so the
    // column could only ever echo it.
    private const string DefaultCurrency = BillingConstants.DefaultCurrency;

    public async Task<GenerateInvoicesForPeriodCommandResult> HandleAsync(
        GenerateInvoicesForPeriodCommand command, CancellationToken ct = default)
    {
        var (year, month) = (command.Year, command.Month);
        var tenantIds = await usageRepo.GetDistinctTenantIdsForMonthAsync(year, month, ct);
        var issuedAt = DateTimeOffset.UtcNow;
        int generated = 0, skipped = 0;

        foreach (var tenantId in tenantIds)
        {
            if (await invoiceRepo.ExistsForPeriodAsync(tenantId, year, month, ct))
            {
                skipped++; // already billed for this period
                continue;
            }

            var records = await usageRepo.GetByTenantAndMonthAsync(tenantId, year, month, ct);
            // ResourceName comes from the UsageRecord snapshot taken at collection time, never a live
            // lookup against the vms/volumes tables.
            var items = records
                .GroupBy(r => (r.ResourceType, r.ResourceId))
                .Select(g => InvoiceItem.Create(
                    g.Key.ResourceType, g.Key.ResourceId, g.First().ResourceName, UsageRecordAggregate.TotalVnd(g), DefaultCurrency))
                .ToList();

            // An invoice states what the tenant used and touches no money
            // (titan-ops architecture/money-and-wallet.md#human-confirmed-debit): the wallet moves only when a person pays it.
            var invoice = InvoiceAggregate.Generate(tenantId, year, month, issuedAt, items, DefaultCurrency);

            // A 0 ₫ bill settles itself: no money moves, and leaving it New would let a bill for nothing
            // age into Overdue.
            if (invoice.Total == 0)
                invoice.AutoSettleZeroTotal();

            var added = await invoiceRepo.TryAddAsync(invoice, ct);

            if (added)
                generated++;
            else
                skipped++; // lost a race against a concurrent generation run for the same period
        }

        return new GenerateInvoicesForPeriodCommandResult(generated, skipped);
    }
}

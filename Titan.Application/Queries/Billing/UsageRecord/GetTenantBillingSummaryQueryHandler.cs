using Titan.Application.Abstractions;
using Titan.Application.Services.Billing;
using Titan.Application.Queries.Billing.UsageRecord.Contracts;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Repositories.Billing;

namespace Titan.Application.Queries.Billing.UsageRecord;

/// Cost is a sum over usage_records, never a mutable running total
/// (titan-ops architecture/money-and-wallet.md#append-only-metering).
public class GetTenantBillingSummaryQueryHandler(IUsageRecordRepository usageRepo, ICurrentUser currentUser)
{
    // See GetVmCostQueryHandler: the read path reports the constant, not the rows' Currency column.
    private const string DefaultCurrency = BillingConstants.DefaultCurrency;

    public async Task<TenantBillingSummaryQueryResult> HandleAsync(
        GetTenantBillingSummaryQuery query, CancellationToken ct = default)
    {
        var (year, month) = (query.Year, query.Month);
        var tenantId = currentUser.RequireTenantId();
        var records = await usageRepo.GetByTenantAndMonthAsync(tenantId, year, month, ct);

        var computeCost = records.Where(r => r.ResourceType == UsageResourceType.Compute).Sum(r => r.Cost);
        // Storage = disk + snapshot. Both occupy datastore and both are billed generically by
        // invoice generation, so both must land in the summary — otherwise TotalCost here understates what
        // the invoice charges, and the tenant is billed for a line their dashboard never showed.
        var storageCost = records
            .Where(r => r.ResourceType is UsageResourceType.Disk or UsageResourceType.Snapshot)
            .Sum(r => r.Cost);

        // Per-VM breakdown is compute-only — disk is reported at the tenant level via
        // storageCost, not attributed to a specific VM (a volume can be detached from any VM, or
        // from none).
        var perVm = records
            .Where(r => r.ResourceType == UsageResourceType.Compute)
            .GroupBy(r => r.ResourceId)
            .Select(g => new VmCostSummaryLine(g.Key, g.First().ResourceName, g.Sum(r => r.Cost)))
            .OrderByDescending(v => v.TotalCost)
            .ToList();

        return new TenantBillingSummaryQueryResult(
            $"{year:D4}-{month:D2}",
            computeCost + storageCost,
            DefaultCurrency,
            computeCost,
            storageCost,
            perVm);
    }
}

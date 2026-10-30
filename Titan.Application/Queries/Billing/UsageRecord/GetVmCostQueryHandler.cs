using Titan.Application.Abstractions;
using Titan.Application.Services.Billing;
using Titan.Application.Queries.Billing.UsageRecord.Contracts;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Billing.UsageRecord;

/// Cost is a sum over usage_records, never a mutable running total
/// (titan-ops architecture/money-and-wallet.md#append-only-metering).
public class GetVmCostQueryHandler(IUsageRecordRepository usageRepo, ICurrentUser currentUser)
{
    // Reports this, not the `Currency` column on the rows it sums: every meter writes this same constant,
    // so the column can only ever echo it.
    private const string DefaultCurrency = BillingConstants.DefaultCurrency;

    public async Task<VmCostQueryResult> HandleAsync(GetVmCostQuery query, CancellationToken ct = default)
    {
        var (vmId, year, month) = (query.VmId, query.Year, query.Month);
        var tenantId = currentUser.RequireTenantId();
        // Defense-in-depth: scope usage to the caller's tenant so cost for another
        // tenant's VM never leaks even if a caller bypasses the endpoint ownership check.
        // Compute-only: disk has its own meter and is not attributed to a VM.
        var records = (await usageRepo.GetByResourceAndMonthAsync(UsageResourceType.Compute, vmId, year, month, ct))
            .Where(r => r.TenantId == tenantId)
            .ToList();

        var breakdown = records
            .OrderBy(r => r.Bucket)
            .Select(r => new UsageBreakdownLine(r.Bucket, r.UsageType, r.Cost))
            .ToList();

        return new VmCostQueryResult(
            vmId,
            $"{year:D4}-{month:D2}",
            breakdown.Sum(b => b.Cost),
            DefaultCurrency,
            breakdown);
    }
}

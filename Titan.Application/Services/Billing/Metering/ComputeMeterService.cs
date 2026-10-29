using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Repositories.Billing;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Repositories.Compute;

namespace Titan.Application.Services.Billing.Metering;

/// Idempotent: a record already present for (vmId, bucket), from a previous tick or another replica, is
/// skipped.
public interface IComputeMeterService : IMeter;

/// A missed tick under-bills and never over-bills: the accepted "approximate" trade-off.
///
/// Price comes from the VM offering (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering). A missing
/// tier is a broken catalog, and this meter refuses to bill through it.
public class ComputeMeterService(
    IVmRepository vmRepo,
    IUsageRecordRepository usageRepo,
    IVmOfferingRepository offeringRepo,
    IOptions<BillingMeterOptions> options) : IComputeMeterService
{
    public string Name => "Compute";

    public async Task<int> RunTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var intervalMinutes = options.Value.IntervalMinutes;
        var billableVms = await vmRepo.ListBillableAsync(ct);
        if (billableVms.Count == 0) return 0;

        // One read for the whole tick rather than one per VM: the catalog is four rows and every VM
        // resolves against it.
        var prices = (await offeringRepo.ListAllAsync(ct)).ToDictionary(o => o.Id, o => o.PricePerHour);

        // Price every VM before writing any row: each insert commits on its own, so refusing halfway would
        // bill the VMs the database happened to return first
        // (titan-ops features/billing/metering/design.md#missing-offering). The whole tick fails instead of
        // skipping this VM or billing it zero: a VM on a missing tier is damaged catalog data, and a stale
        // meter is what /healthz/meters reports.
        var records = new List<UsageRecord>(billableVms.Count);
        foreach (var vm in billableVms)
        {
            if (!prices.TryGetValue(vm.VmOfferingId, out var unitPricePerHour))
                throw new InvalidOperationException(
                    $"VM {vm.Id} references VM offering '{vm.VmOfferingId}', which is not in the catalog. " +
                    "Refusing to bill: a missing offering is a data-integrity fault, not a zero price.");

            records.Add(UsageRecord.CreateCompute(
                vm.Id, vm.Name, vm.TenantId, now, intervalMinutes,
                unitPricePerHour, BillingConstants.DefaultCurrency));
        }

        var inserted = 0;
        foreach (var record in records)
        {
            if (await usageRepo.TryInsertAsync(record, ct))
                inserted++;
        }

        return inserted;
    }
}

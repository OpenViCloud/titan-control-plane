using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Billing.Metering;

/// Idempotent on (volumeId, bucket), same mechanism as the compute meter.
public interface IDiskMeterService : IMeter;

/// Occupying storage is billed whatever the VM's state: a volume attached to a Stopped VM still bills,
/// and so does a detached one. Price comes from the volume's offering
/// (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
public class DiskMeterService(
    IVolumeRepository volumeRepo,
    IUsageRecordRepository usageRepo,
    IVolumeOfferingRepository offeringRepo,
    IOptions<BillingMeterOptions> options) : IDiskMeterService
{
    public string Name => "Disk";

    // The repository decides what bills: seen Allocated or Attached, and no delete asked for.

    public async Task<int> RunTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var intervalMinutes = options.Value.IntervalMinutes;
        var volumes = await volumeRepo.ListBillableAsync(ct);
        if (volumes.Count == 0) return 0;

        var prices = (await offeringRepo.ListAllAsync(ct)).ToDictionary(o => o.Id, o => o.PricePerGbPerHour);

        // Price every volume before writing any row: each insert commits on its own, so refusing halfway
        // would bill the volumes the database happened to return first
        // (titan-ops features/billing/metering/design.md#missing-offering). Same rule as the compute
        // meter: a missing offering is damaged catalog data, not a zero price.
        var records = new List<UsageRecord>(volumes.Count);
        foreach (var volume in volumes)
        {
            if (!prices.TryGetValue(volume.VolumeOfferingId, out var unitPricePerHour))
                throw new InvalidOperationException(
                    $"Volume {volume.Id} references volume offering '{volume.VolumeOfferingId}', which is " +
                    "not in the catalog. Refusing to bill: a missing offering is a data-integrity fault, " +
                    "not a zero price.");

            records.Add(UsageRecord.CreateDisk(
                volume.Id, volume.Name, volume.TenantId, now, intervalMinutes,
                volume.SizeGb, unitPricePerHour, BillingConstants.DefaultCurrency));
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

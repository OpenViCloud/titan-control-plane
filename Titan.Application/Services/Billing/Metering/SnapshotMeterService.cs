using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.SnapshotOffering;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.VolumeSnapshot;
using Titan.Domain.Repositories;

namespace Titan.Application.Services.Billing.Metering;

/// Idempotent on (snapshotId, bucket), same mechanism as the compute and disk meters.
public interface ISnapshotMeterService : IMeter;

/// Snapshots occupy datastore capacity independently of any VM or volume, and neither of the other
/// meters reads them.
///
/// Flat per-snapshot rate: Titan tracks no snapshot size. Snapshots carry no offering id, so the rate
/// comes from the SnapshotOffering.DefaultId row (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
/// A rate of 0 switches snapshot billing off on purpose; a missing row is a data-integrity fault and
/// fails the tick.
public class SnapshotMeterService(
    IVmSnapshotRepository vmSnapshotRepo,
    IVolumeSnapshotRepository volumeSnapshotRepo,
    IUsageRecordRepository usageRepo,
    ISnapshotOfferingRepository offeringRepo,
    IOptions<BillingMeterOptions> options) : ISnapshotMeterService
{
    public string Name => "Snapshot";

    // The repositories decide what bills: seen Ready/BackedUp and no delete asked for. A VM snapshot
    // being reverted stays Ready and still bills.

    public async Task<int> RunTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var intervalMinutes = options.Value.IntervalMinutes;
        var offering = await offeringRepo.FindByIdAsync(SnapshotOffering.DefaultId, ct)
            ?? throw new InvalidOperationException(
                $"Snapshot offering '{SnapshotOffering.DefaultId}' is not in the catalog. Refusing to " +
                "bill snapshots: a missing offering is a data-integrity fault, not a zero price. Set the " +
                "offering's price to 0 to switch snapshot billing off.");
        var unitPricePerHour = offering.PricePerHour;
        var inserted = 0;

        var vmSnapshots = await vmSnapshotRepo.ListBillableAsync(ct);
        foreach (var snap in vmSnapshots)
        {
            var record = UsageRecord.CreateSnapshot(
                snap.Id, snap.Name, snap.TenantId, now, intervalMinutes, unitPricePerHour, BillingConstants.DefaultCurrency);
            if (await usageRepo.TryInsertAsync(record, ct))
                inserted++;
        }

        var volumeSnapshots = await volumeSnapshotRepo.ListBillableAsync(ct);
        foreach (var snap in volumeSnapshots)
        {
            var record = UsageRecord.CreateSnapshot(
                snap.Id, snap.Name, snap.TenantId, now, intervalMinutes, unitPricePerHour, BillingConstants.DefaultCurrency);
            if (await usageRepo.TryInsertAsync(record, ct))
                inserted++;
        }

        return inserted;
    }
}

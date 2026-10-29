using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Billing.UsageRecord;

/// One bucket of one resource, never updated (titan-ops architecture/money-and-wallet.md#append-only-metering). A missed
/// tick under-bills and never over-bills; that approximation is intended.
public class UsageRecord
{
    public Guid Id { get; private set; }
    public UsageResourceType ResourceType { get; private set; }
    public Guid ResourceId { get; private set; }                 // VmId, VolumeId or snapshot id, per ResourceType
    public string ResourceName { get; private set; } = default!; // snapshot at collection time
    public Guid TenantId { get; private set; }
    public DateTimeOffset Bucket { get; private set; }           // floor(CollectedAt, IntervalMinutes), UTC
    public int IntervalMinutes { get; private set; }
    public int Quantity { get; private set; }                    // SizeGb for Disk, 1 for Snapshot, 0 for Compute
    public string UsageType { get; private set; } = default!;    // "RUNNING_VM" | "ALLOCATED_STORAGE" | "SNAPSHOT_STORAGE"
    /// Copied at collect time: this is where price history lives, so changing an offering's price never
    /// reaches back (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
    public Rate UnitPricePerHour { get; private set; }
    /// The raw product for this one interval (rate × hours × quantity), not rounded: VND becomes whole once, on an invoice
    /// line (TotalVnd, titan-ops architecture/money-and-wallet.md#round-once).
    public decimal Cost { get; private set; }
    public string Currency { get; private set; } = default!;
    public DateTimeOffset CollectedAt { get; private set; }

    private UsageRecord() { }

    public static UsageRecord CreateCompute(
        Guid vmId, string vmName, Guid tenantId, DateTimeOffset collectedAt, int intervalMinutes,
        Rate unitPricePerHour, string currency)
    {
        return new UsageRecord
        {
            Id = Guid.NewGuid(),
            ResourceType = UsageResourceType.Compute,
            ResourceId = vmId,
            ResourceName = vmName,
            TenantId = tenantId,
            Bucket = FloorToBucket(collectedAt, intervalMinutes),
            IntervalMinutes = intervalMinutes,
            Quantity = 0,
            UsageType = "RUNNING_VM",
            UnitPricePerHour = unitPricePerHour,
            Cost = RawCost(unitPricePerHour, intervalMinutes, quantity: 1),
            Currency = currency,
            CollectedAt = collectedAt
        };
    }

    public static UsageRecord CreateDisk(
        Guid volumeId, string volumeName, Guid tenantId, DateTimeOffset collectedAt, int intervalMinutes,
        int sizeGb, Rate unitPricePerHour, string currency)
    {
        return new UsageRecord
        {
            Id = Guid.NewGuid(),
            ResourceType = UsageResourceType.Disk,
            ResourceId = volumeId,
            ResourceName = volumeName,
            TenantId = tenantId,
            Bucket = FloorToBucket(collectedAt, intervalMinutes),
            IntervalMinutes = intervalMinutes,
            Quantity = sizeGb,
            UsageType = "ALLOCATED_STORAGE",
            UnitPricePerHour = unitPricePerHour,
            Cost = RawCost(unitPricePerHour, intervalMinutes, sizeGb),
            Currency = currency,
            CollectedAt = collectedAt
        };
    }

    /// Titan tracks no snapshot size, so this bills a flat per-snapshot rate (from SnapshotOffering) pro-rated
    /// to the interval, not per GB.
    public static UsageRecord CreateSnapshot(
        Guid snapshotId, string snapshotName, Guid tenantId, DateTimeOffset collectedAt,
        int intervalMinutes, Rate unitPricePerHour, string currency)
    {
        return new UsageRecord
        {
            Id = Guid.NewGuid(),
            ResourceType = UsageResourceType.Snapshot,
            ResourceId = snapshotId,
            ResourceName = snapshotName,
            TenantId = tenantId,
            Bucket = FloorToBucket(collectedAt, intervalMinutes),
            IntervalMinutes = intervalMinutes,
            Quantity = 1,
            UsageType = "SNAPSHOT_STORAGE",
            UnitPricePerHour = unitPricePerHour,
            Cost = RawCost(unitPricePerHour, intervalMinutes, quantity: 1),
            Currency = currency,
            CollectedAt = collectedAt
        };
    }

    /// Multiplied out before the one division by 60, so a price that divides evenly stays exact (10/60 h is a repeating
    /// decimal); kept to the 10 places of the cost column, so the value in memory is the value stored.
    private static decimal RawCost(Rate unitPricePerHour, int intervalMinutes, int quantity)
    {
        decimal product = unitPricePerHour * (intervalMinutes * (decimal)quantity) / 60m;
        return Math.Round(product, 10, MidpointRounding.AwayFromZero);
    }

    /// Whole VND for a set of records — one invoice line — rounded half away from zero once, on the sum. Rounding each
    /// record instead billed a 1 GB volume (0.26 ₫ a record) and every snapshot (0.3 ₫) at nothing
    /// (titan-ops architecture/money-and-wallet.md#round-once).
    public static long TotalVnd(IEnumerable<UsageRecord> records)
    {
        decimal raw = records.Sum(r => r.Cost);
        return (long)Math.Round(raw, MidpointRounding.AwayFromZero);
    }

    /// e.g. with a 10-minute interval, 09:07 and 09:03 both floor to 09:00. Two ticks in the same window
    /// collide on (resource_type, resource_id, bucket) and the second insert is a no-op.
    private static DateTimeOffset FloorToBucket(DateTimeOffset at, int intervalMinutes)
    {
        var utc = at.ToUniversalTime();
        var totalMinutes = utc.Hour * 60 + utc.Minute;
        var flooredTotalMinutes = totalMinutes - (totalMinutes % intervalMinutes);
        return new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, flooredTotalMinutes / 60, flooredTotalMinutes % 60, 0, TimeSpan.Zero);
    }
}

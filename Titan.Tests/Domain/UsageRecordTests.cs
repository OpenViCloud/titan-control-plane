using Titan.Domain.Values;
using Titan.Domain.Aggregates.UsageRecord;

namespace Titan.Tests.Domain;

// The append-only ledger (titan-ops architecture/money-and-wallet.md#append-only-metering). Each record is one
// fixed-width bucket of one resource (Bucket + IntervalMinutes), never a span that grows, and never mutated
// after creation.
// Cost is computed once, at creation, from a rate snapshot — never recomputed later.
public class UsageRecordTests
{
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    // COMPUTE cost ignores Quantity entirely; only the rate and the interval matter.
    [Fact]
    public void CreateCompute_CostIsRateTimesIntervalFraction()
    {
        var at = new DateTimeOffset(2026, 8, 9, 9, 10, 0, TimeSpan.Zero);

        var record = UsageRecord.CreateCompute(
            ResourceId, "web-01", TenantId, at, intervalMinutes: 10,
            unitPricePerHour: Rate.Of(6000m), currency: "SGD");

        Assert.Equal(UsageResourceType.Compute, record.ResourceType);
        Assert.Equal(0, record.Quantity);
        Assert.Equal(1000m, record.Cost);   // 6000 VND/h × 10/60 h = 1000 VND
    }

    // DISK cost also multiplies by Quantity (SizeGb); COMPUTE does not.
    [Fact]
    public void CreateDisk_CostIsRateTimesIntervalFractionTimesQuantity()
    {
        var at = new DateTimeOffset(2026, 8, 9, 9, 10, 0, TimeSpan.Zero);

        var record = UsageRecord.CreateDisk(
            ResourceId, "data-vol", TenantId, at, intervalMinutes: 10,
            sizeGb: 40, unitPricePerHour: Rate.Of(150m), currency: "SGD");

        Assert.Equal(UsageResourceType.Disk, record.ResourceType);
        Assert.Equal(40, record.Quantity);
        Assert.Equal(1000m, record.Cost);   // 150 VND/GB/h × 10/60 h × 40 GB = 1000 VND
    }

    // Bucket floor: 09:07 and 09:03 both land on 09:00; 09:11 lands on 09:10. This is the whole
    // idempotency mechanism (two ticks in the same window collide on the same bucket).
    [Theory]
    [InlineData(9, 7, 9, 0)]
    [InlineData(9, 3, 9, 0)]
    [InlineData(9, 11, 9, 10)]
    [InlineData(9, 0, 9, 0)]
    [InlineData(9, 59, 9, 50)]
    public void CreateCompute_FloorsBucketToTenMinuteBoundary(int hour, int minute, int expectedHour, int expectedMinute)
    {
        var at = new DateTimeOffset(2026, 8, 9, hour, minute, 0, TimeSpan.Zero);

        var record = UsageRecord.CreateCompute(
            ResourceId, "web-01", TenantId, at, intervalMinutes: 10,
            unitPricePerHour: Rate.Of(0.009m), currency: "SGD");

        Assert.Equal(new DateTimeOffset(2026, 8, 9, expectedHour, expectedMinute, 0, TimeSpan.Zero), record.Bucket);
    }

    // The collection instant itself is preserved verbatim (used for observability), separate
    // from the normalized Bucket used for idempotency and querying.
    [Fact]
    public void CreateCompute_PreservesCollectedAtSeparatelyFromBucket()
    {
        var at = new DateTimeOffset(2026, 8, 9, 9, 7, 33, TimeSpan.Zero);

        var record = UsageRecord.CreateCompute(
            ResourceId, "web-01", TenantId, at, intervalMinutes: 10,
            unitPricePerHour: Rate.Of(0.009m), currency: "SGD");

        Assert.Equal(at, record.CollectedAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.Zero), record.Bucket);
    }

    // A zero rate produces a zero cost and still records the tick, which is what a deliberately zero-rated
    // resource (e.g. snapshot billing switched off) relies on.
    [Fact]
    public void CreateCompute_ZeroRate_RecordsZeroCostAndStillProducesARecord()
    {
        var at = new DateTimeOffset(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

        var record = UsageRecord.CreateCompute(
            ResourceId, "web-01", TenantId, at, intervalMinutes: 10,
            unitPricePerHour: Rate.Of(0m), currency: "SGD");

        Assert.Equal(0m, record.UnitPricePerHour.PerUnit);
        Assert.Equal(0m, record.Cost);
        Assert.Equal(UsageResourceType.Compute, record.ResourceType);
    }

    [Fact]
    public void CreateCompute_SetsIntervalMinutesAndUsageType()
    {
        var record = UsageRecord.CreateCompute(
            ResourceId, "web-01", TenantId, DateTimeOffset.UtcNow, intervalMinutes: 10,
            unitPricePerHour: Rate.Of(0.009m), currency: "SGD");

        Assert.Equal(10, record.IntervalMinutes);
        Assert.Equal("RUNNING_VM", record.UsageType);
    }

    [Fact]
    public void CreateDisk_SetsUsageType()
    {
        var record = UsageRecord.CreateDisk(
            ResourceId, "data-vol", TenantId, DateTimeOffset.UtcNow, intervalMinutes: 10,
            sizeGb: 40, unitPricePerHour: Rate.Of(0.00008773m), currency: "SGD");

        Assert.Equal("ALLOCATED_STORAGE", record.UsageType);
    }
}

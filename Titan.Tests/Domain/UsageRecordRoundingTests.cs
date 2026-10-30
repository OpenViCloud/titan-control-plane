using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/money-and-wallet.md#round-once — a usage record keeps the raw product (rate × hours × quantity); money
// becomes whole VND once, on an invoice line (UsageRecord.TotalVnd), half away from zero. Rounding each 10-minute record
// billed every snapshot and every volume under ~1.9 GB at 0 ₫ and a 2 GB volume at +90%.
public class UsageRecordRoundingTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static UsageRecord Disk(int sizeGb, decimal ratePerGbHour, int minutes = 10, int tick = 0) =>
        UsageRecord.CreateDisk(Guid.NewGuid(), "vol", Guid.NewGuid(), At.AddMinutes(tick * minutes), minutes, sizeGb,
            Rate.Of(ratePerGbHour), "VND");

    [Fact]
    public void ARecord_KeepsTheRawProduct()
    {
        // 1.57914 ₫/GB/h × 10/60 h × 1 GB = 0.26319 ₫ — 0 ₫ when each record was rounded.
        Assert.Equal(0.26319m, Disk(1, 1.57914m).Cost);
    }

    [Fact]
    public void ASnapshotRecord_KeepsTheRawProduct()
    {
        var r = UsageRecord.CreateSnapshot(Guid.NewGuid(), "snap", Guid.NewGuid(), At, 10, Rate.Of(1.8m), "VND");

        Assert.Equal(0.3m, r.Cost);
    }

    [Fact]
    public void TotalVnd_RoundsOnce_OverTheWholeLine()
    {
        // Six 10-minute records of 1 GB = one hour = 1.57914 ₫ → 2 ₫. Rounding each record first gives 0.
        var hour = Enumerable.Range(0, 6).Select(i => Disk(1, 1.57914m, tick: i)).ToList();

        Assert.Equal(2L, UsageRecord.TotalVnd(hour));
    }

    [Fact]
    public void TotalVnd_OfAMonth_OfASmallVolume()
    {
        // 720 h × 1.57914 ₫ = 1136.9808 ₫ → 1137 ₫ (it was 0 ₫ a record).
        var month = Enumerable.Range(0, 720 * 6).Select(i => Disk(1, 1.57914m, tick: i)).ToList();

        Assert.Equal(1137L, UsageRecord.TotalVnd(month));
    }

    [Fact]
    public void TotalVnd_RoundsHalfAwayFromZero()
    {
        // 1 ₫/h over 30 minutes = 0.5 → 1; 0.8 ₫/h over 30 minutes = 0.4 → 0.
        var half = UsageRecord.CreateCompute(Guid.NewGuid(), "vm", Guid.NewGuid(), At, 30, Rate.Of(1m), "VND");
        var below = UsageRecord.CreateCompute(Guid.NewGuid(), "vm", Guid.NewGuid(), At, 30, Rate.Of(0.8m), "VND");

        Assert.Equal(1L, UsageRecord.TotalVnd([half]));
        Assert.Equal(0L, UsageRecord.TotalVnd([below]));
    }

    [Fact]
    public void TotalVnd_OfAWholeVndRate_IsExact()
    {
        var r = UsageRecord.CreateCompute(Guid.NewGuid(), "vm", Guid.NewGuid(), At, 60, Rate.Of(9000m), "VND");

        Assert.Equal(9000L, UsageRecord.TotalVnd([r]));
    }
}

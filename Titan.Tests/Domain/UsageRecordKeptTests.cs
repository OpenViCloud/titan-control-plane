using Titan.Domain.Aggregates.UsageRecord;

namespace Titan.Tests.Domain;

// titan-ops features/billing/metering/design.md#usage-retention: a record is kept 90 days, and a month is read or
// billed only while every bucket of it is still kept.
public class UsageRecordKeptTests
{
    [Fact]
    public void KeptFrom_IsNinetyDaysBeforeNow_InUtc()
    {
        var now = new DateTimeOffset(2026, 7, 30, 7, 0, 0, TimeSpan.FromHours(7));

        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), UsageRecord.KeptFrom(now));
        Assert.Equal(TimeSpan.Zero, UsageRecord.KeptFrom(now).Offset);
    }

    [Fact]
    public void IsAKeptMonth_AMonthThatStartedNinetyOneDaysAgo_IsExpired()
    {
        var now = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

        Assert.False(UsageRecord.IsAKeptMonth(2026, 5, now));
    }

    [Fact]
    public void IsAKeptMonth_AMonthThatStartedExactlyNinetyDaysAgo_IsKept()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

        Assert.True(UsageRecord.IsAKeptMonth(2026, 5, now));
    }

    [Fact]
    public void IsAKeptMonth_AFutureMonth_IsKept()
    {
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

        Assert.True(UsageRecord.IsAKeptMonth(2027, 1, now));
    }

    // The monthly invoice bills the previous month on the 1st, and the billing page opens on the current one: both
    // must always be whole, whatever the day.
    [Fact]
    public void IsAKeptMonth_TheCurrentAndThePreviousMonth_AreKeptOnEveryDayOfTheYear()
    {
        for (var day = new DateTimeOffset(2026, 1, 1, 23, 59, 59, TimeSpan.Zero); day.Year == 2026; day = day.AddDays(1))
        {
            DateTimeOffset previous = day.AddMonths(-1);

            Assert.True(UsageRecord.IsAKeptMonth(day.Year, day.Month, day), $"current month on {day:yyyy-MM-dd}");
            Assert.True(UsageRecord.IsAKeptMonth(previous.Year, previous.Month, day), $"previous month on {day:yyyy-MM-dd}");
        }
    }
}

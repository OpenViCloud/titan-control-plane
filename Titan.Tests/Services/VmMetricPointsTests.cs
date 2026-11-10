using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;

namespace Titan.Tests.Services;

// titan-ops features/vm/design.md#vm-metrics: each pair of adjacent readings makes one point at the later reading; CPU
// and the disk rates come from the counters' differences, memory and the root filesystem from the later reading alone.
public class VmMetricPointsTests
{
    private static readonly Guid VmId = Guid.NewGuid();
    private static readonly DateTimeOffset From = new(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);

    private static VmMetricReading Reading(
        DateTimeOffset at,
        double cpuSeconds,
        string source = "agent",
        long diskRead = 1000,
        long diskWritten = 2000,
        long? memoryTotal = 2_000_000,
        long? memoryAvailable = 1_500_000,
        long? rootFsSize = 10_000_000,
        long? rootFsAvail = 8_000_000) =>
        new(VmId, at, source, cpuSeconds, memoryTotal, memoryAvailable, rootFsSize, rootFsAvail, diskRead, diskWritten);

    [Theory]
    [InlineData(1, 50.0)]
    [InlineData(2, 25.0)]
    public void TwoAgentReadingsSixtySecondsApart_ThirtyCpuSeconds_IsHalfOfOneVcpu(int vcpuCount, double expected)
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(60), 100),
            Reading(From.AddSeconds(120), 130),
        ];

        List<VmMetricPointQueryResult> points = VmMetricPoints.From(readings, From, vcpuCount, VmMetricPoints.OneHour);

        Assert.Equal(2, points.Count);
        Assert.Equal(expected, points[1].CpuPercent);
        Assert.Equal(From.AddSeconds(120), points[1].At);
    }

    [Fact]
    public void TheDiskRates_AreBytesPerSecondOverThePair()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(60), 100, diskRead: 1000, diskWritten: 2000),
            Reading(From.AddSeconds(120), 101, diskRead: 1000, diskWritten: 2000 + 60 * 40960),
        ];

        VmMetricPointQueryResult point = VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour)[1];

        Assert.Equal(0.0, point.DiskReadBytesPerSecond);
        Assert.Equal(40960.0, point.DiskWriteBytesPerSecond);
    }

    [Fact]
    public void MoreCpuThanTheVcpusCouldSpend_IsClampedToOneHundred()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(60), 100),
            Reading(From.AddSeconds(120), 300),
        ];

        Assert.Equal(100.0, VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour)[1].CpuPercent);
    }

    [Fact]
    public void TheFirstReadingWithoutAPredecessor_HasMemoryAndDisk_ButNoCpuNorRates()
    {
        List<VmMetricReading> readings = [Reading(From.AddSeconds(60), 100)];

        VmMetricPointQueryResult point = Assert.Single(VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour));

        Assert.Equal(From.AddSeconds(60), point.At);
        Assert.Null(point.CpuPercent);
        Assert.Null(point.DiskReadBytesPerSecond);
        Assert.Null(point.DiskWriteBytesPerSecond);
        Assert.Equal(500_000, point.MemoryUsedBytes);
        Assert.Equal(2_000_000, point.MemoryTotalBytes);
        Assert.Equal(2_000_000, point.RootFsUsedBytes);
        Assert.Equal(10_000_000, point.RootFsSizeBytes);
    }

    // The reading just before the range lends its counters to the first point and makes no point of its own.
    [Fact]
    public void TheReadingBeforeTheRange_GivesTheFirstPointItsDifference_AndNoPoint()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(-30), 100),
            Reading(From.AddSeconds(30), 130),
        ];

        VmMetricPointQueryResult point = Assert.Single(VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour));

        Assert.Equal(From.AddSeconds(30), point.At);
        Assert.Equal(50.0, point.CpuPercent);
    }

    // A restart resets the counters: the difference would be negative, so the pair says nothing about CPU or disk.
    [Theory]
    [InlineData(90, 1000, 2000)]
    [InlineData(130, 999, 2000)]
    [InlineData(130, 1000, 1999)]
    public void ACounterThatWentDown_LeavesCpuAndBothRatesNull_ButKeepsMemory(double cpu, long read, long written)
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(60), 100, diskRead: 1000, diskWritten: 2000),
            Reading(From.AddSeconds(120), cpu, diskRead: read, diskWritten: written),
        ];

        VmMetricPointQueryResult point = VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour)[1];

        Assert.Null(point.CpuPercent);
        Assert.Null(point.DiskReadBytesPerSecond);
        Assert.Null(point.DiskWriteBytesPerSecond);
        Assert.Equal(500_000, point.MemoryUsedBytes);
    }

    // An agent that starts or stops changes what the counters count.
    [Fact]
    public void TwoReadingsOfDifferentSources_LeaveCpuAndBothRatesNull_ButKeepMemory()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(60), 100, source: "qemu"),
            Reading(From.AddSeconds(120), 130, source: "agent"),
        ];

        VmMetricPointQueryResult point = VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour)[1];

        Assert.Null(point.CpuPercent);
        Assert.Null(point.DiskReadBytesPerSecond);
        Assert.Null(point.DiskWriteBytesPerSecond);
        Assert.Equal(500_000, point.MemoryUsedBytes);
    }

    [Fact]
    public void ARootFilesystemNotReported_IsNullUsed_NotZero()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(60), 100, rootFsSize: null, rootFsAvail: null),
            Reading(From.AddSeconds(120), 130, rootFsSize: 10_000_000, rootFsAvail: null),
        ];

        List<VmMetricPointQueryResult> points = VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour);

        Assert.Null(points[0].RootFsUsedBytes);
        Assert.Null(points[0].RootFsSizeBytes);
        Assert.Null(points[1].RootFsUsedBytes);
        Assert.Equal(10_000_000, points[1].RootFsSizeBytes);
    }

    [Fact]
    public void MemoryWithoutAvailable_IsNullUsed()
    {
        List<VmMetricReading> readings = [Reading(From.AddSeconds(60), 100, memoryAvailable: null)];

        VmMetricPointQueryResult point = Assert.Single(VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour));

        Assert.Null(point.MemoryUsedBytes);
        Assert.Equal(2_000_000, point.MemoryTotalBytes);
    }

    [Theory]
    [InlineData("1h", 60)]
    [InlineData("24h", 300)]
    [InlineData("7d", 1800)]
    public void EachRange_HasItsStep(string range, int stepSeconds)
    {
        Assert.True(VmMetricPoints.IsARange(range));
        Assert.Equal(stepSeconds, VmMetricPoints.StepSecondsFor(range));
    }

    [Theory]
    [InlineData("2h")]
    [InlineData("1H")]
    [InlineData("")]
    public void AnyOtherWord_IsNotARange(string range)
    {
        Assert.False(VmMetricPoints.IsARange(range));
    }

    [Fact]
    public void TheRangeLengths_AreOneHourOneDayAndSevenDays()
    {
        Assert.Equal(TimeSpan.FromHours(1), VmMetricPoints.LengthOf("1h"));
        Assert.Equal(TimeSpan.FromHours(24), VmMetricPoints.LengthOf("24h"));
        Assert.Equal(TimeSpan.FromDays(7), VmMetricPoints.LengthOf("7d"));
    }

    // 24h: one point per five minutes at the start of the bucket, each field the mean of the bucket's non-null values.
    [Fact]
    public void TwentyFourHours_AveragesEachFiveMinuteBucket_AtTheBucketStart()
    {
        // From is on a five-minute boundary; readings every 60 s, CPU 30 s then 60 s per minute on one vCPU.
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(0), 0, memoryAvailable: 1_000_000),
            Reading(From.AddSeconds(60), 30, memoryAvailable: 1_000_000),
            Reading(From.AddSeconds(120), 90, memoryAvailable: 1_500_000),
            Reading(From.AddSeconds(300), 120, memoryAvailable: 1_900_000),
        ];

        List<VmMetricPointQueryResult> points = VmMetricPoints.From(readings, From, 1, VmMetricPoints.TwentyFourHours);

        Assert.Equal(2, points.Count);
        VmMetricPointQueryResult first = points[0];
        Assert.Equal(From, first.At);
        // CPU: null (no predecessor), 50, 100 ⇒ mean of the non-null values.
        Assert.Equal(75.0, first.CpuPercent);
        // Memory used: 1_000_000, 1_000_000, 500_000 ⇒ 833_333.33 rounded.
        Assert.Equal(833_333, first.MemoryUsedBytes);
        Assert.Equal(2_000_000, first.MemoryTotalBytes);

        VmMetricPointQueryResult second = points[1];
        Assert.Equal(From.AddSeconds(300), second.At);
        // 30 s over 180 s.
        Assert.Equal(30.0 / 180.0 * 100, second.CpuPercent!.Value, 6);
        Assert.Equal(100_000, second.MemoryUsedBytes);
    }

    [Fact]
    public void ABucketWithOnlyNulls_IsNull_AndAnEmptyBucket_IsNoPoint()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(10), 100),
            // Two buckets later, a different source: the pair gives no CPU.
            Reading(From.AddSeconds(3700), 130, source: "qemu"),
        ];

        List<VmMetricPointQueryResult> points = VmMetricPoints.From(readings, From, 1, VmMetricPoints.SevenDays);

        Assert.Equal(2, points.Count);
        Assert.Equal(From, points[0].At);
        Assert.Equal(From.AddSeconds(3600), points[1].At);
        Assert.Null(points[0].CpuPercent);
        Assert.Null(points[1].CpuPercent);
    }

    [Fact]
    public void OneHour_KeepsTheRawPoints_AtTheirSampleTimes()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(5), 100),
            Reading(From.AddSeconds(35), 110),
        ];

        List<VmMetricPointQueryResult> points = VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour);

        Assert.Equal([From.AddSeconds(5), From.AddSeconds(35)], points.Select(p => p.At));
    }

    [Fact]
    public void NoReading_IsNoPoint()
    {
        Assert.Empty(VmMetricPoints.From([], From, 1, VmMetricPoints.OneHour));
    }

    [Fact]
    public void ReadingsOutOfOrder_AreReadOldestFirst()
    {
        List<VmMetricReading> readings =
        [
            Reading(From.AddSeconds(120), 130),
            Reading(From.AddSeconds(60), 100),
        ];

        List<VmMetricPointQueryResult> points = VmMetricPoints.From(readings, From, 1, VmMetricPoints.OneHour);

        Assert.Equal(50.0, points[1].CpuPercent);
    }
}

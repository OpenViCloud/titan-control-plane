using Titan.Application.Abstractions;
using Titan.Application.Exceptions;
using Titan.Application.Queries.Compute.Vm.Contracts;

namespace Titan.Application.Queries.Compute.Vm;

// Turns stored readings into the points a tenant sees (titan-ops features/vm/design.md#vm-metrics). Pure: no clock, no
// store, so every rule is a unit test.
public static class VmMetricPoints
{
    public const string OneHour = "1h";
    public const string TwentyFourHours = "24h";
    public const string SevenDays = "7d";

    public static bool IsARange(string range) =>
        range == OneHour || range == TwentyFourHours || range == SevenDays;

    public static int StepSecondsFor(string range)
    {
        switch (range)
        {
            case OneHour:
                return 60;
            case TwentyFourHours:
                return 300;
            case SevenDays:
                return 1800;
            default:
                throw new BadRequestException("VALIDATION_ERROR", "range must be 1h, 24h or 7d.");
        }
    }

    public static TimeSpan LengthOf(string range)
    {
        switch (range)
        {
            case OneHour:
                return TimeSpan.FromHours(1);
            case TwentyFourHours:
                return TimeSpan.FromHours(24);
            case SevenDays:
                return TimeSpan.FromDays(7);
            default:
                throw new BadRequestException("VALIDATION_ERROR", "range must be 1h, 24h or 7d.");
        }
    }

    /// readings: the VM's readings from `from` on, and may start with the one reading before `from`, which lends its
    /// counters to the first point and makes no point of its own. 1h keeps one point per reading; 24h and 7d average
    /// the points of each step.
    public static List<VmMetricPointQueryResult> From(
        IReadOnlyList<VmMetricReading> readings, DateTimeOffset from, int vcpuCount, string range)
    {
        List<VmMetricReading> ordered = readings.OrderBy(r => r.SampledAt).ToList();

        var raw = new List<VmMetricPointQueryResult>();
        for (int i = 0; i < ordered.Count; i++)
        {
            VmMetricReading current = ordered[i];
            if (current.SampledAt < from)
            {
                continue;
            }
            VmMetricReading? previous = null;
            if (i > 0)
            {
                previous = ordered[i - 1];
            }
            raw.Add(PointOf(previous, current, vcpuCount));
        }

        if (range == OneHour)
        {
            return raw;
        }
        return Average(raw, StepSecondsFor(range));
    }

    private static VmMetricPointQueryResult PointOf(VmMetricReading? previous, VmMetricReading current, int vcpuCount)
    {
        long? memoryUsed = null;
        if (current.MemoryTotalBytes != null && current.MemoryAvailableBytes != null)
        {
            memoryUsed = current.MemoryTotalBytes.Value - current.MemoryAvailableBytes.Value;
        }
        long? rootFsUsed = null;
        if (current.RootFsSizeBytes != null && current.RootFsAvailBytes != null)
        {
            rootFsUsed = current.RootFsSizeBytes.Value - current.RootFsAvailBytes.Value;
        }

        double? cpuPercent = null;
        double? readPerSecond = null;
        double? writePerSecond = null;
        if (previous != null && CountersCompare(previous, current))
        {
            double seconds = (current.SampledAt - previous.SampledAt).TotalSeconds;
            if (vcpuCount > 0)
            {
                double percent = (current.CpuSeconds - previous.CpuSeconds) / (seconds * vcpuCount) * 100;
                cpuPercent = Math.Clamp(percent, 0, 100);
            }
            readPerSecond = (current.DiskReadBytes - previous.DiskReadBytes) / seconds;
            writePerSecond = (current.DiskWrittenBytes - previous.DiskWrittenBytes) / seconds;
        }

        return new VmMetricPointQueryResult(current.SampledAt, cpuPercent, memoryUsed, current.MemoryTotalBytes,
            rootFsUsed, current.RootFsSizeBytes, readPerSecond, writePerSecond);
    }

    // A different source counts different things, and a counter that went down was reset by a restart: either way the
    // difference means nothing.
    private static bool CountersCompare(VmMetricReading previous, VmMetricReading current)
    {
        if (previous.Source != current.Source)
        {
            return false;
        }
        if (current.SampledAt <= previous.SampledAt)
        {
            return false;
        }
        return current.CpuSeconds >= previous.CpuSeconds
               && current.DiskReadBytes >= previous.DiskReadBytes
               && current.DiskWrittenBytes >= previous.DiskWrittenBytes;
    }

    private static List<VmMetricPointQueryResult> Average(List<VmMetricPointQueryResult> raw, int stepSeconds)
    {
        var points = new List<VmMetricPointQueryResult>();
        IEnumerable<IGrouping<long, VmMetricPointQueryResult>> steps = raw
            .GroupBy(p => p.At.ToUnixTimeSeconds() / stepSeconds)
            .OrderBy(g => g.Key);
        foreach (IGrouping<long, VmMetricPointQueryResult> step in steps)
        {
            List<VmMetricPointQueryResult> inStep = step.ToList();
            points.Add(new VmMetricPointQueryResult(
                DateTimeOffset.FromUnixTimeSeconds(step.Key * stepSeconds),
                MeanOf(inStep.Select(p => p.CpuPercent)),
                RoundedMeanOf(inStep.Select(p => p.MemoryUsedBytes)),
                RoundedMeanOf(inStep.Select(p => p.MemoryTotalBytes)),
                RoundedMeanOf(inStep.Select(p => p.RootFsUsedBytes)),
                RoundedMeanOf(inStep.Select(p => p.RootFsSizeBytes)),
                MeanOf(inStep.Select(p => p.DiskReadBytesPerSecond)),
                MeanOf(inStep.Select(p => p.DiskWriteBytesPerSecond))));
        }
        return points;
    }

    private static double? MeanOf(IEnumerable<double?> values)
    {
        List<double> present = values.Where(v => v != null).Select(v => v!.Value).ToList();
        if (present.Count == 0)
        {
            return null;
        }
        return present.Average();
    }

    private static long? RoundedMeanOf(IEnumerable<long?> values)
    {
        List<long> present = values.Where(v => v != null).Select(v => v!.Value).ToList();
        if (present.Count == 0)
        {
            return null;
        }
        return (long)Math.Round(present.Average(), MidpointRounding.AwayFromZero);
    }
}

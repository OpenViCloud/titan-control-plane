namespace Titan.Infrastructure.Persistence;

/// <summary>
/// Not a domain aggregate: one row of a VM's metric time series (titan-ops features/vm/design.md#vm-metrics). Written by
/// the worker's batch, read back as points, deleted after seven days.
/// </summary>
public class VmMetricSample
{
    public Guid VmId { get; set; }
    public DateTimeOffset SampledAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public double CpuSeconds { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? MemoryAvailableBytes { get; set; }
    public long? RootFsSizeBytes { get; set; }
    public long? RootFsAvailBytes { get; set; }
    public long DiskReadBytes { get; set; }
    public long DiskWrittenBytes { get; set; }
}

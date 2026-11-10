namespace Titan.Application.Commands.Compute.Vm.Contracts;

// One batch of the worker's metric readings for one cluster, all read at SampledAt (titan-ops features/vm/design.md#vm-metrics).
// Each sample names its VM by Incus instance name.
public record RecordVmMetricsCommand(string ClusterId, DateTimeOffset SampledAt, IReadOnlyList<VmMetricsSample> Vms);

public record VmMetricsSample(
    string ExternalId,
    string Source,
    double CpuSeconds,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? RootFsSizeBytes,
    long? RootFsAvailBytes,
    long DiskReadBytes,
    long DiskWrittenBytes);

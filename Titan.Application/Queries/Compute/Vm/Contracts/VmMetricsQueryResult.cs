namespace Titan.Application.Queries.Compute.Vm.Contracts;

// A VM's usage over one range (titan-ops features/vm/design.md#vm-metrics). A step with no sample has no point: the
// portal draws a gap.
public record VmMetricsQueryResult(string Range, int StepSeconds, List<VmMetricPointQueryResult> Points);

// At is the sample time for 1h and the start of the step for 24h and 7d. A null field is a number Titan does not have:
// CPU and the disk rates across a restart or a change of source, memory and the root filesystem without the guest agent.
public record VmMetricPointQueryResult(
    DateTimeOffset At,
    double? CpuPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? RootFsUsedBytes,
    long? RootFsSizeBytes,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond);

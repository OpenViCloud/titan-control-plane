namespace Titan.Application.Abstractions;

/// The VM metric time series, table core.vm_metric_samples (titan-ops features/vm/design.md#vm-metrics): a read-side table,
/// not an aggregate. The worker sends one reading per VM per minute; the tenant reads them back as points.
public interface IVmMetricStore
{
    /// The VMs of this cluster that carry these Incus instance names, keyed by name, whatever their status. A name no VM
    /// of the cluster carries is absent.
    Task<Dictionary<string, Guid>> FindVmIdsAsync(
        string clusterId, IReadOnlyCollection<string> externalIds, CancellationToken ct = default);

    /// Writes the readings. A reading whose (VmId, SampledAt) is already stored is skipped, so a batch sent again writes
    /// nothing.
    Task AddAsync(IReadOnlyList<VmMetricReading> readings, CancellationToken ct = default);

    /// The VM's readings sampled at or after from, oldest first, preceded by the newest reading before from when there
    /// is one: the first point of a range needs the reading before it to have a difference.
    Task<List<VmMetricReading>> ListFromAsync(Guid vmId, DateTimeOffset from, CancellationToken ct = default);

    /// Deletes every reading sampled before cutoff. Returns the rows removed.
    Task<int> DeleteSampledBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

/// One reading of a VM's counters. CpuSeconds, DiskReadBytes and DiskWrittenBytes only grow while the VM runs; the
/// four memory and root filesystem numbers are null when Incus reports none (the root filesystem only with the guest
/// agent, memory sometimes without it). Source is "agent" or "qemu".
public record VmMetricReading(
    Guid VmId,
    DateTimeOffset SampledAt,
    string Source,
    double CpuSeconds,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? RootFsSizeBytes,
    long? RootFsAvailBytes,
    long DiskReadBytes,
    long DiskWrittenBytes);

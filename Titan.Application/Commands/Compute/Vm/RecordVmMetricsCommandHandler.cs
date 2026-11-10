using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Application.Commands.Compute.Vm;

// The worker's metric batch for one cluster (titan-ops features/vm/design.md#vm-metrics). Changes no resource's status:
// a name no VM of the cluster carries (another cluster's VM, an instance Titan did not make) is dropped without an
// error, and a reading already stored is skipped, so the worker may send a batch again. Authenticated by
// X-Internal-Token at the endpoint, acting for no tenant.
public class RecordVmMetricsCommandHandler(IVmMetricStore store)
{
    public async Task HandleAsync(RecordVmMetricsCommand command, CancellationToken ct = default)
    {
        if (command.Vms.Count == 0)
        {
            return;
        }

        List<string> names = command.Vms.Select(v => v.ExternalId).Distinct().ToList();
        Dictionary<string, Guid> vmIds = await store.FindVmIdsAsync(command.ClusterId, names, ct);

        var readings = new List<VmMetricReading>();
        var written = new HashSet<Guid>();
        foreach (VmMetricsSample sample in command.Vms)
        {
            if (!vmIds.TryGetValue(sample.ExternalId, out Guid vmId))
            {
                continue;
            }
            // A name twice in one batch would be the same key twice; the first one is kept.
            if (!written.Add(vmId))
            {
                continue;
            }
            readings.Add(new VmMetricReading(vmId, command.SampledAt, sample.Source, sample.CpuSeconds,
                sample.MemoryTotalBytes, sample.MemoryAvailableBytes, sample.RootFsSizeBytes, sample.RootFsAvailBytes,
                sample.DiskReadBytes, sample.DiskWrittenBytes));
        }

        if (readings.Count == 0)
        {
            return;
        }
        await store.AddAsync(readings, ct);
    }
}

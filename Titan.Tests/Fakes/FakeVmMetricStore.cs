using Titan.Application.Abstractions;

namespace Titan.Tests.Fakes;

// The VM metric table in memory, for handler tests. VmMetricSampleDbTests measures the same answers on Postgres.
public class FakeVmMetricStore : IVmMetricStore
{
    private readonly List<(string ClusterId, string ExternalId, Guid VmId)> _vms = [];

    public List<VmMetricReading> Readings { get; } = [];

    public void AddVm(string clusterId, string externalId, Guid vmId)
    {
        _vms.Add((clusterId, externalId, vmId));
    }

    public Task<Dictionary<string, Guid>> FindVmIdsAsync(
        string clusterId, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        var found = new Dictionary<string, Guid>();
        foreach (var vm in _vms)
        {
            if (vm.ClusterId == clusterId && externalIds.Contains(vm.ExternalId))
            {
                found[vm.ExternalId] = vm.VmId;
            }
        }
        return Task.FromResult(found);
    }

    public Task AddAsync(IReadOnlyList<VmMetricReading> readings, CancellationToken ct = default)
    {
        foreach (VmMetricReading reading in readings)
        {
            bool stored = Readings.Any(r => r.VmId == reading.VmId && r.SampledAt == reading.SampledAt);
            if (!stored)
            {
                Readings.Add(reading);
            }
        }
        return Task.CompletedTask;
    }

    public Task<List<VmMetricReading>> ListFromAsync(Guid vmId, DateTimeOffset from, CancellationToken ct = default)
    {
        List<VmMetricReading> result = [];
        VmMetricReading? before = Readings
            .Where(r => r.VmId == vmId && r.SampledAt < from)
            .OrderByDescending(r => r.SampledAt)
            .FirstOrDefault();
        if (before != null)
        {
            result.Add(before);
        }
        result.AddRange(Readings.Where(r => r.VmId == vmId && r.SampledAt >= from).OrderBy(r => r.SampledAt));
        return Task.FromResult(result);
    }

    public Task<int> DeleteSampledBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        int removed = Readings.RemoveAll(r => r.SampledAt < cutoff);
        return Task.FromResult(removed);
    }
}

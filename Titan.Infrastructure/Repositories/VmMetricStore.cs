using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Titan.Application.Abstractions;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

// core.vm_metric_samples (titan-ops features/vm/design.md#vm-metrics). The insert and the retention DELETE are SQL the
// InMemory provider cannot run; VmMetricSampleDbTests measures them on Postgres.
public class VmMetricStore(TitanDbContext db) : IVmMetricStore
{
    // Ten parameters a row; Postgres takes at most 65535 parameters in one statement.
    private const int RowsPerStatement = 1000;

    public async Task<Dictionary<string, Guid>> FindVmIdsAsync(
        string clusterId, IReadOnlyCollection<string> externalIds, CancellationToken ct = default)
    {
        List<string> names = externalIds.ToList();
        var rows = await db.Vms
            .AsNoTracking()
            .Where(v => v.ClusterId == clusterId && v.ExternalId != null && names.Contains(v.ExternalId))
            .Select(v => new { v.Id, v.ExternalId })
            .ToListAsync(ct);

        var found = new Dictionary<string, Guid>();
        foreach (var row in rows)
        {
            found[row.ExternalId!] = row.Id;
        }
        return found;
    }

    // ON CONFLICT DO NOTHING rather than read-then-insert: two sends of one batch at once still write one row each.
    public async Task AddAsync(IReadOnlyList<VmMetricReading> readings, CancellationToken ct = default)
    {
        for (int start = 0; start < readings.Count; start += RowsPerStatement)
        {
            int end = Math.Min(start + RowsPerStatement, readings.Count);
            var sql = new StringBuilder(
                "INSERT INTO core.vm_metric_samples (vm_id, sampled_at, source, cpu_seconds, memory_total_bytes, "
                + "memory_available_bytes, root_fs_size_bytes, root_fs_avail_bytes, disk_read_bytes, disk_written_bytes) VALUES ");
            var parameters = new List<NpgsqlParameter>();
            for (int i = start; i < end; i++)
            {
                VmMetricReading reading = readings[i];
                if (i > start)
                {
                    sql.Append(", ");
                }
                sql.Append($"(@vm{i}, @at{i}, @source{i}, @cpu{i}, @memTotal{i}, @memAvail{i}, @fsSize{i}, @fsAvail{i}, "
                           + $"@read{i}, @written{i})");
                parameters.Add(Parameter($"vm{i}", NpgsqlDbType.Uuid, reading.VmId));
                // Npgsql writes a timestamptz only from a DateTimeOffset at offset zero.
                parameters.Add(Parameter($"at{i}", NpgsqlDbType.TimestampTz, reading.SampledAt.ToUniversalTime()));
                parameters.Add(Parameter($"source{i}", NpgsqlDbType.Text, reading.Source));
                parameters.Add(Parameter($"cpu{i}", NpgsqlDbType.Double, reading.CpuSeconds));
                parameters.Add(Parameter($"memTotal{i}", NpgsqlDbType.Bigint, reading.MemoryTotalBytes));
                parameters.Add(Parameter($"memAvail{i}", NpgsqlDbType.Bigint, reading.MemoryAvailableBytes));
                parameters.Add(Parameter($"fsSize{i}", NpgsqlDbType.Bigint, reading.RootFsSizeBytes));
                parameters.Add(Parameter($"fsAvail{i}", NpgsqlDbType.Bigint, reading.RootFsAvailBytes));
                parameters.Add(Parameter($"read{i}", NpgsqlDbType.Bigint, reading.DiskReadBytes));
                parameters.Add(Parameter($"written{i}", NpgsqlDbType.Bigint, reading.DiskWrittenBytes));
            }
            sql.Append(" ON CONFLICT (vm_id, sampled_at) DO NOTHING");
            await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters, ct);
        }
    }

    public async Task<List<VmMetricReading>> ListFromAsync(Guid vmId, DateTimeOffset from, CancellationToken ct = default)
    {
        DateTimeOffset fromUtc = from.ToUniversalTime();
        VmMetricSample? before = await db.VmMetricSamples
            .AsNoTracking()
            .Where(s => s.VmId == vmId && s.SampledAt < fromUtc)
            .OrderByDescending(s => s.SampledAt)
            .FirstOrDefaultAsync(ct);
        List<VmMetricSample> inRange = await db.VmMetricSamples
            .AsNoTracking()
            .Where(s => s.VmId == vmId && s.SampledAt >= fromUtc)
            .OrderBy(s => s.SampledAt)
            .ToListAsync(ct);

        var readings = new List<VmMetricReading>();
        if (before != null)
        {
            readings.Add(ReadingOf(before));
        }
        foreach (VmMetricSample sample in inRange)
        {
            readings.Add(ReadingOf(sample));
        }
        return readings;
    }

    public async Task<int> DeleteSampledBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        DateTimeOffset cutoffUtc = cutoff.ToUniversalTime();
        return await db.VmMetricSamples.Where(s => s.SampledAt < cutoffUtc).ExecuteDeleteAsync(ct);
    }

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value)
    {
        object stored = DBNull.Value;
        if (value != null)
        {
            stored = value;
        }
        return new NpgsqlParameter(name, type) { Value = stored };
    }

    private static VmMetricReading ReadingOf(VmMetricSample sample) =>
        new(sample.VmId, sample.SampledAt, sample.Source, sample.CpuSeconds, sample.MemoryTotalBytes,
            sample.MemoryAvailableBytes, sample.RootFsSizeBytes, sample.RootFsAvailBytes, sample.DiskReadBytes,
            sample.DiskWrittenBytes);
}

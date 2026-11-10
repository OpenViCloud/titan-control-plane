using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The VM metric table on Postgres (titan-ops features/vm/design.md#vm-metrics): the insert that skips a row already
/// stored, the read with the sample before the range, the retention DELETE and the cascade from the VM. InMemory runs
/// none of the SQL. Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VmMetricSampleDbTests(PostgresFixture pg)
{
    private static string InstanceName() => "vm" + Guid.NewGuid().ToString("N")[..26];

    private async Task<(Guid VmId, string ExternalId)> SeedVmAsync(string clusterId)
    {
        string externalId = InstanceName();
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "metrics-db", "m", "tpl", clusterId, 20);
        vm.CreateSucceeded(vm.Operation.PendingId!.Value, externalId, VmStatus.Running, "10.0.0.5", null);
        await using TitanDbContext db = pg.NewDbContext();
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return (vm.Id, externalId);
    }

    private static VmMetricReading Reading(Guid vmId, DateTimeOffset at, double cpuSeconds, long? rootFsSize = 10222313472) =>
        new(vmId, at, "agent", cpuSeconds, 2059284480, 1780793344, rootFsSize, rootFsSize == null ? null : 8203649024,
            443663360, 440393728);

    [Fact]
    public async Task ABatch_FindsTheClustersVms_WritesEveryNumber_AndTheSameBatchAgainWritesNothing()
    {
        (Guid vmId, string externalId) = await SeedVmAsync("cluster-metrics-a");
        (_, string otherClusters) = await SeedVmAsync("cluster-metrics-b");
        DateTimeOffset sampledAt = new DateTimeOffset(2026, 11, 10, 21, 0, 0, TimeSpan.FromHours(7));

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var store = new VmMetricStore(db);
            Dictionary<string, Guid> found = await store.FindVmIdsAsync("cluster-metrics-a",
                [externalId, otherClusters, InstanceName()]);
            Assert.Equal(new Dictionary<string, Guid> { [externalId] = vmId }, found);

            await store.AddAsync([Reading(vmId, sampledAt, 496.24, rootFsSize: null)]);
            await store.AddAsync([Reading(vmId, sampledAt, 999)]);
        }

        await using TitanDbContext read = pg.NewDbContext();
        VmMetricSample row = Assert.Single(await read.VmMetricSamples.Where(s => s.VmId == vmId).ToListAsync());
        Assert.Equal(sampledAt, row.SampledAt);
        Assert.Equal(TimeSpan.Zero, row.SampledAt.Offset);
        Assert.Equal("agent", row.Source);
        Assert.Equal(496.24, row.CpuSeconds);
        Assert.Equal(2059284480, row.MemoryTotalBytes);
        Assert.Equal(1780793344, row.MemoryAvailableBytes);
        Assert.Null(row.RootFsSizeBytes);
        Assert.Null(row.RootFsAvailBytes);
        Assert.Equal(443663360, row.DiskReadBytes);
        Assert.Equal(440393728, row.DiskWrittenBytes);
    }

    [Fact]
    public async Task TheRead_IsTheRangeOldestFirst_AfterTheNewestSampleBeforeIt()
    {
        (Guid vmId, _) = await SeedVmAsync("cluster-metrics-a");
        DateTimeOffset from = new(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await new VmMetricStore(db).AddAsync(
            [
                Reading(vmId, from.AddMinutes(2), 30),
                Reading(vmId, from.AddMinutes(-2), 10),
                Reading(vmId, from.AddMinutes(1), 20),
                Reading(vmId, from.AddMinutes(-1), 15),
            ]);
        }

        await using TitanDbContext read = pg.NewDbContext();
        List<VmMetricReading> readings = await new VmMetricStore(read).ListFromAsync(vmId, from);

        Assert.Equal([15.0, 20.0, 30.0], readings.Select(r => r.CpuSeconds));
    }

    [Fact]
    public async Task Retention_DeletesTheEightDayOldSample_AndKeepsTheOneDayOld()
    {
        (Guid vmId, _) = await SeedVmAsync("cluster-metrics-a");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await new VmMetricStore(db).AddAsync([Reading(vmId, now.AddDays(-8), 1), Reading(vmId, now.AddDays(-1), 2)]);
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            int removed = await VmMetricRetention.RunOnceAsync(new VmMetricStore(db), 7, now, NullLogger.Instance, default);
            Assert.True(removed >= 1, $"removed {removed}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal([2.0], await read.VmMetricSamples.Where(s => s.VmId == vmId).Select(s => s.CpuSeconds).ToListAsync());
    }

    [Fact]
    public async Task DeletingTheVmRow_DeletesItsSamples()
    {
        (Guid vmId, _) = await SeedVmAsync("cluster-metrics-a");
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await new VmMetricStore(db).AddAsync([Reading(vmId, DateTimeOffset.UtcNow, 1)]);
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM core.vms WHERE id = {vmId}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        Assert.False(await read.VmMetricSamples.AnyAsync(s => s.VmId == vmId));
    }
}

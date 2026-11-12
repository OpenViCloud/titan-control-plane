using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// Passes of a background job at once, each with its own DbContext as each control plane pod has: they take turns on
/// the job's lock, so the day's backup commands come out once and no pass fails. Without the lock, eight passes
/// can deadlock on Postgres (40P01): each inserts its new commands in the order of their random ids, so two passes lock
/// the same keys in different orders (titan-ops architecture/background-jobs.md#background-jobs). Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class JobPassesAtOnceDbTests(PostgresFixture pg)
{
    private const int Passes = 8;
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    private async Task<(Vm Vm, Volume Volume)> StoredVmAndVolumeAsync()
    {
        Vm vm = Vm.Create(Tenant, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], "m-backup-passes", "tpl", "cluster-a", 20);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        Volume volume = Volume.Create(Tenant, $"vol-{Guid.NewGuid():N}"[..14], "cluster-a", "standard", 10);
        volume.MarkCreated($"ext-{volume.Id:N}");
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO core.vm_offerings (id, name, vcpu_count, memory_mb, price_per_hour) VALUES ('m-backup-passes', 'm', 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();
        return (vm, volume);
    }

    private static ScheduleBackupsCommandHandler SchedulerOn(TitanDbContext db)
    {
        return new ScheduleBackupsCommandHandler(new VmRepository(db), new VolumeRepository(db),
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System), new PostgresJobPassLock(db),
            TimeProvider.System);
    }

    // Every pass is released at once; each returns the exception it threw, or null.
    private async Task<Exception?[]> ScheduleAtOnceAsync()
    {
        using var start = new SemaphoreSlim(0, Passes);
        Task<Exception?>[] passes = Enumerable.Range(0, Passes).Select(async _ =>
        {
            await using TitanDbContext db = pg.NewDbContext();
            await start.WaitAsync();
            try
            {
                await SchedulerOn(db).HandleAsync(new ScheduleBackupsCommand());
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }).ToArray();
        start.Release(Passes);
        return await Task.WhenAll(passes);
    }

    [Fact]
    public async Task ScheduledByEveryPodAtOnce_EachVmAndVolumeGetsOneBackupOfTheDay_AndNoPassFails()
    {
        (Vm vm, Volume volume) = await StoredVmAndVolumeAsync();

        Exception?[] failures = await ScheduleAtOnceAsync();

        Assert.All(failures, Assert.Null);
        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(1, await read.ProviderCommands.AsNoTracking().CountAsync(c => c.Key.StartsWith($"vm-backup-{vm.Id}-")));
        Assert.Equal(1, await read.ProviderCommands.AsNoTracking().CountAsync(c => c.Key.StartsWith($"volume-backup-{volume.Id}-")));
    }

    // The lock a pass takes before it reads: a second pass of the job waits until the first saved.
    [Fact]
    public async Task ASecondPassOfAJob_WaitsForTheLock_UntilTheFirstPassSaves()
    {
        string job = $"test-{Guid.NewGuid():N}";
        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        await new PostgresJobPassLock(first).TakeAsync(job, CancellationToken.None);

        Task secondTakes = new PostgresJobPassLock(second).TakeAsync(job, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(secondTakes.IsCompleted);

        await first.SaveChangesAsync();
        await secondTakes.WaitAsync(TimeSpan.FromSeconds(10));
        await second.SaveChangesAsync();
    }

    // Another job's lock does not wait for this one.
    [Fact]
    public async Task TwoJobs_DoNotWaitForEachOther()
    {
        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        await new PostgresJobPassLock(first).TakeAsync($"test-{Guid.NewGuid():N}", CancellationToken.None);

        await new PostgresJobPassLock(second).TakeAsync($"test-{Guid.NewGuid():N}", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        await first.SaveChangesAsync();
        await second.SaveChangesAsync();
    }
}

using Titan.Application.Services.Platform.Backup;
using Microsoft.EntityFrameworkCore;
using Moq;
using Titan.Application.Options;
using Titan.Application.Queries.Admin.Backup;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.BackupObservation;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// One item per VM and volume on Incus, from the backup cache: what the page shows for tenants' disks
// (titan-ops features/backup/design.md#incus-backup).
public class ListDiskBackupsQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<IVolumeRepository> _volumes = new();
    private readonly FakeBackupRecordRepository _records = new();
    private readonly FakeBackupObservationRepository _observations = new();

    private Task<IReadOnlyList<BackupItemQueryResult>> RunAsync(params string[] clusters) =>
        new ListDiskBackupsQueryHandler(_vms.Object, _volumes.Object, _records, _observations, new FixedClock(Now),
                new ClusterOptions { Clusters = clusters.Select(id => new ClusterConfig { Id = id, Name = "Cluster " + id }).ToList() })
            .HandleAsync(new ListDiskBackupsQuery());

    private void Holds(List<Vm> vms, List<Volume> volumes)
    {
        _vms.Setup(r => r.ListAllAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(vms);
        _volumes.Setup(r => r.ListOnIncusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(volumes);
    }

    private void ReadMinutesAgo(int minutes)
    {
        var observation = BackupObservation.Create(BackupLayout.IncusObservationKind, BackupLayout.IncusObservationName);
        observation.RecordRead(BackupReadOutcome.Read, null, Now.AddMinutes(-minutes));
        _observations.Rows.Add(observation);
    }

    private void Backup(string kind, Guid id, Guid tenantId, TimeSpan age, long size = 100) =>
        _records.Rows.Add(BackupRecord.Create(kind, id.ToString(), $"k{age.Ticks}", Now - age, size, BackupRecordStatus.Complete, tenantId));

    private static Vm RunningVm(string name)
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), name, "m", "tpl-1", "mock", 20);
        vm.MarkRunning("vm-" + name, "10.0.0.2");
        return vm;
    }

    private static Volume CreatedVolume(string name)
    {
        Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, name, "mock", "std", 10);
        volume.MarkCreated("vol-" + name);
        return volume;
    }

    [Fact]
    public async Task AVmWithAFreshBackup_IsOk_WithItsNameTenantAndNewestBackup()
    {
        Vm vm = RunningVm("web-1");
        Holds([vm], []);
        ReadMinutesAgo(1);
        Backup(BackupRecord.VmKind, vm.Id, vm.TenantId, TimeSpan.FromHours(30), size: 1);
        Backup(BackupRecord.VmKind, vm.Id, vm.TenantId, TimeSpan.FromHours(3), size: 667);

        BackupItemQueryResult item = Assert.Single(await RunAsync());

        Assert.Equal(BackupRecord.VmKind, item.Kind);
        Assert.Equal("web-1", item.Name);
        Assert.Equal(vm.TenantId, item.TenantId);
        Assert.Equal(Now.AddHours(-3), item.TakenAt);
        Assert.Equal(667, item.SizeBytes);
        Assert.Equal((BackupHealth.Ok, (string?)null), (item.Health, item.Reason));
    }

    [Fact]
    public async Task AVolumeWhoseNewestBackupIsPastTheLimit_IsStale()
    {
        Volume volume = CreatedVolume("data-1");
        Holds([], [volume]);
        ReadMinutesAgo(1);
        Backup(BackupRecord.VolumeKind, volume.Id, volume.TenantId, BackupHealthPolicy.DiskStaleAfter + TimeSpan.FromMinutes(1));

        BackupItemQueryResult item = Assert.Single(await RunAsync());

        Assert.Equal(BackupRecord.VolumeKind, item.Kind);
        Assert.Equal((BackupHealth.Stale, BackupHealthPolicy.BackupTooOld), (item.Health, item.Reason));
    }

    // The page lists the volumes Incus holds, not the billed ones: a VM or volume an admin restored from a backup is
    // never billed and is listed like any other, Missing until its own first backup
    // (titan-ops features/backup/design.md#restore-as-new-resource). On the real repositories (InMemory), since a stub
    // answers whichever list the handler asks.
    [Fact]
    public async Task ARestoredVmAndVolume_AreListed_LikeAnyOther()
    {
        using var db = new TitanDbContext(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"backups-page-{Guid.NewGuid()}").Options);
        Vm restoredVm = Vm.RestoreFromBackup(RunningVm("web-1"), "web-1-restored");
        restoredVm.CreateSucceeded(restoredVm.Operation.PendingId!.Value, "vm-web-1-restored", VmStatus.Stopped, null, null);
        Volume restoredVolume = Volume.RestoreFromBackup(CreatedVolume("data-1"), "data-1-restored");
        restoredVolume.MarkCreated("vol-data-1-restored");
        db.Vms.Add(restoredVm);
        db.Volumes.Add(restoredVolume);
        await db.SaveChangesAsync();
        ReadMinutesAgo(1);
        var handler = new ListDiskBackupsQueryHandler(new VmRepository(db), new VolumeRepository(db), _records, _observations,
            new FixedClock(Now), new ClusterOptions());

        IReadOnlyList<BackupItemQueryResult> items = await handler.HandleAsync(new ListDiskBackupsQuery());

        Assert.Equal(
            new[] { (BackupRecord.VmKind, "web-1-restored"), (BackupRecord.VolumeKind, "data-1-restored") },
            items.Select(i => (i.Kind, i.Name)).ToArray());
        Assert.All(items, i => Assert.Equal((BackupHealth.Missing, BackupHealthPolicy.NoBackup), (i.Health, i.Reason)));
        Assert.False(restoredVolume.IsBilled);
    }

    [Fact]
    public async Task AVmOnIncusWithNoBackup_IsMissing()
    {
        Holds([RunningVm("web-1")], []);
        ReadMinutesAgo(1);

        BackupItemQueryResult item = Assert.Single(await RunAsync());

        Assert.Equal((BackupHealth.Missing, BackupHealthPolicy.NoBackup), (item.Health, item.Reason));
        Assert.Null(item.TakenAt);
    }

    // Another VM's backup is not this one's.
    [Fact]
    public async Task ABackupOfAnotherVm_DoesNotCount()
    {
        Vm vm = RunningVm("web-1");
        Holds([vm], []);
        ReadMinutesAgo(1);
        Backup(BackupRecord.VmKind, Guid.NewGuid(), vm.TenantId, TimeSpan.FromHours(1));

        Assert.Equal(BackupHealth.Missing, Assert.Single(await RunAsync()).Health);
    }

    // "Could not read" is never "no backup".
    [Fact]
    public async Task ABucketThatCouldNotBeRead_IsUnknown_NotMissing()
    {
        Vm vm = RunningVm("web-1");
        Holds([vm], []);
        var observation = BackupObservation.Create(BackupLayout.IncusObservationKind, BackupLayout.IncusObservationName);
        observation.RecordUnreadable("access denied (AccessDenied)", Now.AddMinutes(-1));
        _observations.Rows.Add(observation);

        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.BucketUnreadable),
            (Assert.Single(await RunAsync()).Health, Assert.Single(await RunAsync()).Reason));
    }

    [Fact]
    public async Task NothingReadYet_IsUnknown_AndAReadingTooOld_IsUnknown()
    {
        Holds([RunningVm("web-1")], []);
        Assert.Equal(BackupHealthPolicy.NotReadYet, Assert.Single(await RunAsync()).Reason);

        ReadMinutesAgo((int)BackupHealthPolicy.ReadingMaxAge.TotalMinutes + 1);
        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.StatusTooOld),
            (Assert.Single(await RunAsync()).Health, Assert.Single(await RunAsync()).Reason));
    }

    // Only what Incus holds is protected: a VM still being created, or already deleted, has no disk to back up.
    [Fact]
    public async Task AVmNotOnIncus_IsNotListed()
    {
        Vm creating = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-2", "m", "tpl-1", "mock", 20);
        Vm gone = RunningVm("web-3");
        gone.Delete(Guid.NewGuid());
        gone.MarkDeleted();
        Holds([creating, gone], []);
        ReadMinutesAgo(1);

        Assert.Empty(await RunAsync());
    }

    // What an admin restores from (titan-ops features/backup/design.md#restore-as-new-resource): each VM and volume item
    // names its Titan id and every Complete archive of its own, newest first. An unfinished upload is no archive, and
    // another resource's archive is not this one's, under the same key or not.
    [Fact]
    public async Task EachVmAndVolume_CarriesItsId_AndEveryCompleteArchiveOfItsOwn_NewestFirst()
    {
        Vm vm = RunningVm("web-1");
        Volume volume = CreatedVolume("data-1");
        Holds([vm], [volume]);
        ReadMinutesAgo(1);
        _records.Rows.Add(BackupRecord.Create(BackupRecord.VmKind, vm.Id.ToString(), "20261024T020000Z", Now.AddDays(-2), 11, BackupRecordStatus.Complete, vm.TenantId));
        _records.Rows.Add(BackupRecord.Create(BackupRecord.VmKind, vm.Id.ToString(), "20261026T020000Z", Now.AddHours(-10), 33, BackupRecordStatus.Complete, vm.TenantId));
        _records.Rows.Add(BackupRecord.Create(BackupRecord.VmKind, vm.Id.ToString(), "20261025T020000Z", Now.AddDays(-1).AddHours(-10), 22, BackupRecordStatus.Complete, vm.TenantId));
        _records.Rows.Add(BackupRecord.Create(BackupRecord.VmKind, vm.Id.ToString(), "20261026T110000Z", Now.AddHours(-1), 5, BackupRecordStatus.Incomplete, vm.TenantId));
        _records.Rows.Add(BackupRecord.Create(BackupRecord.VmKind, Guid.NewGuid().ToString(), "20261026T020000Z", Now.AddHours(-10), 99, BackupRecordStatus.Complete, vm.TenantId));
        _records.Rows.Add(BackupRecord.Create(BackupRecord.VolumeKind, volume.Id.ToString(), "20261026T030000Z", Now.AddHours(-9), 44, BackupRecordStatus.Complete, volume.TenantId));

        IReadOnlyList<BackupItemQueryResult> items = await RunAsync();

        BackupItemQueryResult vmItem = Assert.Single(items, i => i.Kind == BackupRecord.VmKind);
        Assert.Equal(vm.Id, vmItem.ResourceId);
        Assert.Equal(
            new[]
            {
                ("20261026T020000Z", Now.AddHours(-10), 33L),
                ("20261025T020000Z", Now.AddDays(-1).AddHours(-10), 22L),
                ("20261024T020000Z", Now.AddDays(-2), 11L),
            },
            vmItem.Archives.Select(a => (a.BackupKey, a.TakenAt, a.SizeBytes)).ToArray());
        BackupItemQueryResult volumeItem = Assert.Single(items, i => i.Kind == BackupRecord.VolumeKind);
        Assert.Equal(volume.Id, volumeItem.ResourceId);
        Assert.Equal(new[] { ("20261026T030000Z", 44L) }, volumeItem.Archives.Select(a => (a.BackupKey, a.SizeBytes)).ToArray());
    }

    [Fact]
    public async Task AVmWithNoArchive_CarriesItsId_AndAnEmptyList()
    {
        Vm vm = RunningVm("web-1");
        Holds([vm], []);
        ReadMinutesAgo(1);

        BackupItemQueryResult item = Assert.Single(await RunAsync());

        Assert.Equal(vm.Id, item.ResourceId);
        Assert.NotNull(item.Archives);
        Assert.Empty(item.Archives);
    }

    // A cluster's config is restored with the whole cluster, never as a new resource: no Titan id, no archive to pick.
    [Fact]
    public async Task AClusterItem_HasNoResourceId_AndNoArchives()
    {
        Holds([], []);
        ReadMinutesAgo(2);
        _records.Rows.Add(BackupRecord.Create(BackupRecord.ClusterKind, "a", "k1", Now.AddHours(-2), 300, BackupRecordStatus.Complete, null));

        BackupItemQueryResult item = Assert.Single(await RunAsync("a"));

        Assert.Null(item.ResourceId);
        Assert.NotNull(item.Archives);
        Assert.Empty(item.Archives);
    }

    // Each configured cluster is an item of its own: its config is what rebuilds the networks after losing the host
    // (titan-ops features/backup/design.md#cluster-backup).
    [Fact]
    public async Task EachConfiguredCluster_IsAnItem_WithItsNewestConfig_AndNoTenant()
    {
        Holds([], []);
        ReadMinutesAgo(2);
        _records.Rows.Add(BackupRecord.Create(BackupRecord.ClusterKind, "a", "k1", Now.AddHours(-2), 300, BackupRecordStatus.Complete, null));

        IReadOnlyList<BackupItemQueryResult> items = await RunAsync("a", "b");

        BackupItemQueryResult a = Assert.Single(items, i => i.Name == "Cluster a");
        Assert.Equal(BackupRecord.ClusterKind, a.Kind);
        Assert.Null(a.TenantId);
        Assert.Equal(Now.AddHours(-2), a.TakenAt);
        Assert.Equal(BackupHealth.Ok, a.Health);
        Assert.Equal(BackupHealth.Missing, Assert.Single(items, i => i.Name == "Cluster b").Health);
    }
}

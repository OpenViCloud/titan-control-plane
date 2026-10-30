using Moq;
using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Queries.Admin.Backup;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.BackupObservation;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Repositories;
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

    private Task<IReadOnlyList<BackupItemQueryResult>> RunAsync() =>
        new ListDiskBackupsQueryHandler(_vms.Object, _volumes.Object, _records, _observations, new FixedClock(Now))
            .HandleAsync(new ListDiskBackupsQuery());

    private void Holds(List<Vm> vms, List<Volume> volumes)
    {
        _vms.Setup(r => r.ListAllAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(vms);
        _volumes.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(volumes);
    }

    private void ReadMinutesAgo(int minutes)
    {
        var observation = BackupObservation.Create(BackupLayout.IncusObservationKind, BackupLayout.IncusObservationName);
        observation.RecordRead(BackupReadOutcome.Read, null, Now.AddMinutes(-minutes));
        _observations.Rows.Add(observation);
    }

    private void Backup(string kind, Guid id, Guid tenantId, TimeSpan age, long size = 100) =>
        _records.Rows.Add(BackupRecord.CreateForTenant(kind, id.ToString(), tenantId, $"k{age.Ticks}", Now - age, size, Now));

    private static Vm RunningVm(string name)
    {
        Vm vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), name, "m", "tpl-1", "mock", 20);
        vm.MarkRunning("vm-" + name, "10.0.0.2");
        return vm;
    }

    private static Volume CreatedVolume(string name)
    {
        Volume volume = Volume.CreateUnchecked(Guid.NewGuid(), name, "mock", "std", 10);
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
        Vm creating = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "web-2", "m", "tpl-1", "mock", 20);
        Vm gone = RunningVm("web-3");
        gone.MarkDeleted();
        Holds([creating, gone], []);
        ReadMinutesAgo(1);

        Assert.Empty(await RunAsync());
    }
}

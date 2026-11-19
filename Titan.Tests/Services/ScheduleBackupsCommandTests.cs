using Moq;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Repositories;
using Titan.Infrastructure.ProviderCommands;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// One backup a day of every VM and volume Incus holds, every tenant
// (titan-ops features/backup/design.md#incus-backup).
public class ScheduleBackupsCommandTests
{
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<IVolumeRepository> _volumes = new();
    private readonly FakeProviderCommandRepository _rows = new();
    private readonly FakeJobPassLock _lock = new();
    // 06:30 on the 27th in Asia/Saigon is still 23:30 on the 26th in UTC: the day in the key is the UTC one.
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 27, 6, 30, 0, TimeSpan.FromHours(7)));

    private ScheduleBackupsCommandHandler Handler(params string[] clusters) =>
        new(_vms.Object, _volumes.Object, new ProviderCommandQueue(_rows, _clock), _rows, _lock, _clock,
            new ClusterOptions { Clusters = clusters.Select(id => new ClusterConfig { Id = id }).ToList() });

    private void Holds(List<Vm> vms, List<Volume> volumes)
    {
        // The repository's SQL is Vm.HasAnInstance (VmRepositoryStateTests pins the two to agree).
        _vms.Setup(r => r.ListWithAnInstanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(vms.Where(v => v.HasAnInstance).ToList());
        _volumes.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(volumes);
    }

    private static Vm RunningVm(string externalId)
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-1", "m", "tpl-1", "mock", 20);
        vm.MarkRunning(externalId, "10.0.0.2");
        return vm;
    }

    private static Volume CreatedVolume(string externalId)
    {
        Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, "data-1", "mock", "std", 10);
        volume.MarkCreated(externalId);
        return volume;
    }

    [Fact]
    public async Task EveryVmAndVolumeOnIncus_GetsTodaysBackup_WithItsTenantsProject()
    {
        Vm vm = RunningVm("vm-a");
        Volume volume = CreatedVolume("vol-a");
        Holds([vm], [volume]);

        await Handler().HandleAsync(new ScheduleBackupsCommand());

        Assert.Equal(
            new[] { $"vm-backup-{vm.Id}-20261026", $"volume-backup-{volume.Id}-20261026" },
            _rows.Rows.Select(r => r.Key).ToArray());
        ProviderCommand vmRow = _rows.Rows[0];
        Assert.Equal(ProviderCommandKinds.VmBackup, vmRow.Kind);
        Assert.Equal("mock", vmRow.ClusterId);
        Assert.Contains($"\"projectName\":\"t-{vm.TenantId:N}\"", vmRow.Payload);
        Assert.Contains($"\"tenantId\":\"{vm.TenantId}\"", vmRow.Payload);
        Assert.Contains("\"externalVmId\":\"vm-a\"", vmRow.Payload);
        Assert.Contains("\"externalVolumeId\":\"vol-a\"", _rows.Rows[1].Payload);
        _vms.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // A stopped VM still has a disk to lose.
    [Fact]
    public async Task AStoppedVm_IsBackedUpToo()
    {
        Vm vm = RunningVm("vm-a");
        vm.MarkStopped();
        Holds([vm], []);

        await Handler().HandleAsync(new ScheduleBackupsCommand());

        Assert.Single(_rows.Rows);
    }

    // Nothing on Incus yet, or no longer: there is no disk to back up.
    [Fact]
    public async Task AVmNotOnIncus_GetsNoBackup()
    {
        Vm creating = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-2", "m", "tpl-1", "mock", 20);
        Vm gone = RunningVm("vm-gone");
        gone.Delete(Guid.NewGuid());
        gone.MarkDeleted();
        Holds([creating, gone], []);

        await Handler().HandleAsync(new ScheduleBackupsCommand());

        Assert.Empty(_rows.Rows);
    }

    [Fact]
    public async Task RunTwiceTheSameDay_AddsNoRow()
    {
        Holds([RunningVm("vm-a")], [CreatedVolume("vol-a")]);

        await Handler().HandleAsync(new ScheduleBackupsCommand());
        await Handler().HandleAsync(new ScheduleBackupsCommand());

        Assert.Equal(2, _rows.Rows.Count);
    }

    // Two control plane pods may run the pass at once: the second waits for the first to save, then reads what it
    // saved (JobPassesAtOnceDbTests). So the lock comes before the pass reads anything.
    [Fact]
    public async Task ThePass_TakesItsJobLock_BeforeItReadsAnything()
    {
        Holds([RunningVm("vm-a")], []);
        _vms.Setup(r => r.ListWithAnInstanceAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Assert.Equal(new[] { ScheduleBackupsCommandHandler.Job }, _lock.Taken))
            .ReturnsAsync([]);

        await Handler().HandleAsync(new ScheduleBackupsCommand());

        Assert.Equal(new[] { "backup-schedule" }, _lock.Taken);
    }

    private static Vm RunningVmIn(string clusterId, string externalId)
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-1", "m", "tpl-1", clusterId, 20);
        vm.MarkRunning(externalId, "10.0.0.2");
        return vm;
    }

    private ProviderCommand Row(string key) => _rows.Rows.Single(r => r.Key == key);

    private static void Ends(ProviderCommand row, bool failed)
    {
        Guid lease = Guid.NewGuid();
        row.Lease(lease, row.CreatedAt);
        if (failed)
        {
            row.Fail(lease, "boom", retryable: false, row.CreatedAt);
            return;
        }
        row.Complete(lease);
    }

    // The cluster's config is read after the day's disks, so it is newer than every archive of the day
    // (titan-ops features/backup/design.md#cluster-backup).
    [Fact]
    public async Task AClusterWithNoDiskToBackUp_GetsTodaysConfigBackup_OfNoTenantAndNoResource()
    {
        Holds([], []);

        await Handler("mock").HandleAsync(new ScheduleBackupsCommand());

        ProviderCommand row = Assert.Single(_rows.Rows);
        Assert.Equal("cluster-backup-mock-20261026", row.Key);
        Assert.Equal(ProviderCommandKinds.ClusterBackup, row.Kind);
        Assert.Equal("mock", row.ClusterId);
        Assert.Null(row.TenantId);
        Assert.Null(row.ResourceId);
    }

    [Fact]
    public async Task TheConfigBackup_WaitsUntilEveryDiskBackupOfItsClusterEndedToday()
    {
        Vm vm = RunningVmIn("mock", "vm-a");
        Volume volume = CreatedVolume("vol-a");
        Holds([vm], [volume]);

        await Handler("mock").HandleAsync(new ScheduleBackupsCommand());
        bool askedWhileBothWait = _rows.Rows.Any(r => r.Kind == ProviderCommandKinds.ClusterBackup);
        Ends(Row($"vm-backup-{vm.Id}-20261026"), failed: false);
        await Handler("mock").HandleAsync(new ScheduleBackupsCommand());
        bool askedWhileOneWaits = _rows.Rows.Any(r => r.Kind == ProviderCommandKinds.ClusterBackup);
        Ends(Row($"volume-backup-{volume.Id}-20261026"), failed: false);
        await Handler("mock").HandleAsync(new ScheduleBackupsCommand());

        Assert.False(askedWhileBothWait);
        Assert.False(askedWhileOneWaits);
        Assert.Equal(ProviderCommandStatus.Pending, Row("cluster-backup-mock-20261026").Status);
    }

    // A disk backup that failed has ended for the day's config: the pass asks the config and runs the disk again, so one
    // disk that keeps failing does not keep the cluster's config out of the bucket.
    [Fact]
    public async Task AFailedDiskBackup_HasEnded_SoTheConfigIsAsked_AndTheDiskRunsAgain()
    {
        Vm vm = RunningVmIn("mock", "vm-a");
        Holds([vm], []);
        await Handler("mock").HandleAsync(new ScheduleBackupsCommand());
        Ends(Row($"vm-backup-{vm.Id}-20261026"), failed: true);

        await Handler("mock").HandleAsync(new ScheduleBackupsCommand());

        Assert.Equal(ProviderCommandStatus.Pending, Row($"vm-backup-{vm.Id}-20261026").Status);
        Assert.Equal(ProviderCommandStatus.Pending, Row("cluster-backup-mock-20261026").Status);
    }

    [Fact]
    public async Task ADiskBackupOfAnotherCluster_DoesNotHoldThisClustersConfig()
    {
        Vm elsewhere = RunningVmIn("other", "vm-b");
        Holds([elsewhere], []);

        await Handler("mock", "other").HandleAsync(new ScheduleBackupsCommand());

        Assert.Contains(_rows.Rows, r => r.Key == "cluster-backup-mock-20261026");
        Assert.DoesNotContain(_rows.Rows, r => r.Key == "cluster-backup-other-20261026");
    }
}

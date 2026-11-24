using System.Text.Json;
using Moq;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;
using Titan.Infrastructure.ProviderCommands;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// One restore drill a day per cluster, asked once the day's disk backups have ended
// (titan-ops features/backup/design.md#restore-drill).
public class ScheduleRestoreDrillTests
{
    private readonly Mock<IVmRepository> _vms = new();
    private readonly Mock<IVolumeRepository> _volumes = new();
    private readonly FakeProviderCommandRepository _rows = new();
    private readonly FakeJobPassLock _lock = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 26, 1, 0, 0, TimeSpan.Zero));

    private ScheduleBackupsCommandHandler Handler(bool enabled = true, params string[] clusters) =>
        new(_vms.Object, _volumes.Object, new ProviderCommandQueue(_rows, _clock), _rows, _lock, _clock,
            new ClusterOptions { Clusters = (clusters.Length == 0 ? ["mock"] : clusters).Select(id => new ClusterConfig { Id = id }).ToList() },
            new RestoreDrillOptions { Enabled = enabled });

    private void Holds(List<Vm> vms, List<Volume> volumes)
    {
        _vms.Setup(r => r.ListWithAnInstanceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(vms.Where(v => v.HasAnInstance).ToList());
        _volumes.Setup(r => r.ListOnIncusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(volumes);
    }

    private static Vm RunningVm(string externalId, string cluster = "mock")
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-" + externalId, "m", "tpl-1", cluster, 20);
        vm.MarkRunning(externalId, "10.0.0.2");
        return vm;
    }

    private static Volume CreatedVolume(string externalId, string cluster = "mock")
    {
        Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, "data-" + externalId, cluster, "std", 10);
        volume.MarkCreated(externalId);
        return volume;
    }

    private ProviderCommand Row(string key) => _rows.Rows.Single(r => r.Key == key);

    private IEnumerable<ProviderCommand> Drills => _rows.Rows.Where(r => r.Kind == ProviderCommandKinds.RestoreDrill);

    private static void Ends(ProviderCommand row, bool failed)
    {
        Guid lease = Guid.NewGuid();
        row.Lease(lease, row.AvailableAt);
        if (failed)
        {
            row.Fail(lease, "boom", retryable: false, row.AvailableAt);
            return;
        }
        row.Complete(lease);
    }

    private void At(int day, int hour) => _clock.Now = new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.Zero);

    private async Task PassAsync(ScheduleBackupsCommandHandler handler) => await handler.HandleAsync(new ScheduleBackupsCommand());

    private static string Day(int day) => $"202610{day:00}";

    // Every disk of the cluster has its day's backup row Done.
    private async Task TheDaysBackupsAreDone(List<Vm> vms, List<Volume> volumes, int day = 26)
    {
        Holds(vms, volumes);
        await PassAsync(Handler());
        foreach (Vm vm in vms)
        {
            Ends(Row($"vm-backup-{vm.Id}-{Day(day)}"), failed: false);
        }
        foreach (Volume volume in volumes)
        {
            Ends(Row($"volume-backup-{volume.Id}-{Day(day)}"), failed: false);
        }
    }

    [Fact]
    public async Task ByDefault_NoDrillIsAsked()
    {
        Vm vm = RunningVm("vm-a");
        await TheDaysBackupsAreDone([vm], []);

        await PassAsync(new ScheduleBackupsCommandHandler(_vms.Object, _volumes.Object, new ProviderCommandQueue(_rows, _clock), _rows, _lock, _clock,
            new ClusterOptions { Clusters = [new ClusterConfig { Id = "mock" }] }));

        Assert.Empty(Drills);
    }

    [Fact]
    public async Task WhenOff_NoDrillIsAsked_EvenWithBackupsDone()
    {
        Vm vm = RunningVm("vm-a");
        await TheDaysBackupsAreDone([vm], []);

        await PassAsync(Handler(enabled: false));

        Assert.Empty(Drills);
    }

    // AC-1: after the day's disk backups ended, one drill, of no tenant and no resource (a lane of its own), with what the
    // worker needs in the payload.
    [Fact]
    public async Task OnceTheDaysDisksHaveEnded_OneDrillIsAsked_OfNoTenantAndNoResource()
    {
        Vm vm = RunningVm("vm-a");
        await TheDaysBackupsAreDone([vm], []);

        await PassAsync(Handler());

        ProviderCommand row = Assert.Single(Drills);
        Assert.Equal("restore-drill-mock-20261026", row.Key);
        Assert.Equal("mock", row.ClusterId);
        Assert.Null(row.TenantId);
        Assert.Null(row.ResourceId);
        using JsonDocument payload = JsonDocument.Parse(row.Payload);
        Assert.Equal("vm", payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal(vm.Id, payload.RootElement.GetProperty("resourceId").GetGuid());
        Assert.Equal("vm-a", payload.RootElement.GetProperty("externalId").GetString());
        Assert.Equal(vm.Name, payload.RootElement.GetProperty("name").GetString());
        Assert.Equal($"t-{vm.TenantId:N}", payload.RootElement.GetProperty("projectName").GetString());
        Assert.Equal(vm.TenantId, payload.RootElement.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task WhileADiskBackupOfTheClusterIsStillOpen_NoDrillIsAsked()
    {
        Vm vm = RunningVm("vm-a");
        Volume volume = CreatedVolume("vol-a");
        await TheDaysBackupsAreDone([vm], [volume]);
        // Reopen the volume's by asking again with a fresh key is not possible: make a second disk whose row is open.
        Vm late = RunningVm("vm-b");
        Holds([vm, late], [volume]);

        await PassAsync(Handler());

        Assert.Empty(Drills);
    }

    [Fact]
    public async Task WhenNoDiskBackupOfTheClusterIsDone_NoDrillIsAsked()
    {
        Vm vm = RunningVm("vm-a");
        Holds([vm], []);
        await PassAsync(Handler());
        Ends(Row($"vm-backup-{vm.Id}-20261026"), failed: true);

        await PassAsync(Handler());

        Assert.Empty(Drills);
    }

    // AC-1: the drill is asked once a day, and a Failed one is not asked again that day (unlike a disk backup).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADrillThatEnded_IsNotAskedAgainTheSameDay(bool failed)
    {
        Vm vm = RunningVm("vm-a");
        await TheDaysBackupsAreDone([vm], []);
        await PassAsync(Handler());
        Ends(Row("restore-drill-mock-20261026"), failed);
        ProviderCommandStatus ended = Row("restore-drill-mock-20261026").Status;

        At(26, 5);
        await PassAsync(Handler());
        await PassAsync(Handler());

        Assert.Single(Drills);
        Assert.Equal(ended, Row("restore-drill-mock-20261026").Status);
    }

    [Fact]
    public async Task ADiskWhoseBackupFailedToday_IsNotChosen_BecauseItHasNoArchiveOfToday()
    {
        Vm good = RunningVm("vm-a");
        Vm failed = RunningVm("vm-b");
        Holds([good, failed], []);
        await PassAsync(Handler());
        Ends(Row($"vm-backup-{good.Id}-20261026"), failed: false);
        Ends(Row($"vm-backup-{failed.Id}-20261026"), failed: true);

        await PassAsync(Handler());

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(Drills).Payload);
        Assert.Equal(good.Id, payload.RootElement.GetProperty("resourceId").GetGuid());
    }

    // AC-9: volumes first when nothing was drilled; then the one not drilled the longest.
    [Fact]
    public async Task WithNothingDrilledYet_AVolumeGoesBeforeAVm()
    {
        Vm vm = RunningVm("vm-a");
        Volume volume = CreatedVolume("vol-a");
        await TheDaysBackupsAreDone([vm], [volume]);

        await PassAsync(Handler());

        using JsonDocument payload = JsonDocument.Parse(Assert.Single(Drills).Payload);
        Assert.Equal("volume", payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal(volume.Id, payload.RootElement.GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task TheNextDay_TheDiskNotDrilledTheLongest_IsChosen()
    {
        Vm vm = RunningVm("vm-a");
        Volume volume = CreatedVolume("vol-a");
        await TheDaysBackupsAreDone([vm], [volume]);
        await PassAsync(Handler());
        Ends(Row("restore-drill-mock-20261026"), failed: false);

        At(27, 1);
        await TheDaysBackupsAreDone([vm], [volume], day: 27);
        await PassAsync(Handler());

        ProviderCommand second = Row("restore-drill-mock-20261027");
        using JsonDocument payload = JsonDocument.Parse(second.Payload);
        Assert.Equal("vm", payload.RootElement.GetProperty("kind").GetString());
        Assert.Equal(vm.Id, payload.RootElement.GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task ADiskWhoseDrillFailed_IsStillRotatedOut_SoOneBrokenDiskDoesNotTakeEveryDay()
    {
        Vm vm = RunningVm("vm-a");
        Volume volume = CreatedVolume("vol-a");
        await TheDaysBackupsAreDone([vm], [volume]);
        await PassAsync(Handler());
        Ends(Row("restore-drill-mock-20261026"), failed: true);

        At(27, 1);
        await TheDaysBackupsAreDone([vm], [volume], day: 27);
        await PassAsync(Handler());

        using JsonDocument payload = JsonDocument.Parse(Row("restore-drill-mock-20261027").Payload);
        Assert.Equal("vm", payload.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task AnotherClustersDisks_AreNotThisClustersChoice()
    {
        Vm elsewhere = RunningVm("vm-b", "other");
        Vm here = RunningVm("vm-a");
        Holds([elsewhere, here], []);
        await PassAsync(Handler(true, "mock", "other"));
        Ends(Row($"vm-backup-{elsewhere.Id}-20261026"), failed: false);
        Ends(Row($"vm-backup-{here.Id}-20261026"), failed: false);

        await PassAsync(Handler(true, "mock", "other"));

        Assert.Equal(2, Drills.Count());
        using JsonDocument mock = JsonDocument.Parse(Row("restore-drill-mock-20261026").Payload);
        using JsonDocument other = JsonDocument.Parse(Row("restore-drill-other-20261026").Payload);
        Assert.Equal(here.Id, mock.RootElement.GetProperty("resourceId").GetGuid());
        Assert.Equal(elsewhere.Id, other.RootElement.GetProperty("resourceId").GetGuid());
    }
}

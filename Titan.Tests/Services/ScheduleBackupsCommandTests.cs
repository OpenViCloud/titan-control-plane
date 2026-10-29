using Moq;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Aggregates.Platform.ProviderCommand;
using Titan.Domain.Repositories.Compute;
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
    // 06:30 on the 27th in Asia/Saigon is still 23:30 on the 26th in UTC: the day in the key is the UTC one.
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 27, 6, 30, 0, TimeSpan.FromHours(7)));

    private ScheduleBackupsCommandHandler Handler() =>
        new(_vms.Object, _volumes.Object, new ProviderCommandQueue(_rows, _clock), _clock);

    private void Holds(List<Vm> vms, List<Volume> volumes)
    {
        _vms.Setup(r => r.ListAllAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(vms);
        _volumes.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(volumes);
    }

    private static Vm RunningVm(string externalId)
    {
        Vm vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "web-1", "m", "tpl-1", "mock", 20);
        vm.MarkRunning(externalId, "10.0.0.2");
        return vm;
    }

    private static Volume CreatedVolume(string externalId)
    {
        Volume volume = Volume.CreateUnchecked(Guid.NewGuid(), "data-1", "mock", "std", 10);
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
        Vm creating = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "web-2", "m", "tpl-1", "mock", 20);
        Vm gone = RunningVm("vm-gone");
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
}

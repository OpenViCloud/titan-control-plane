using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Admin.Backup;

/// Asks the worker for today's backup of every VM and custom volume Incus holds, across tenants
/// (titan-ops features/backup/design.md#incus-backup). Runs every hour: the
/// day is in the command's key, so a day's backup that ran is not asked again and one that failed is. The commands are
/// saved once, at the end.
public class ScheduleBackupsCommandHandler(
    IVmRepository vms,
    IVolumeRepository volumes,
    IProviderCommandQueue commands,
    TimeProvider clock)
{
    public async Task HandleAsync(ScheduleBackupsCommand command, CancellationToken ct = default)
    {
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        foreach (var vm in await vms.ListAllAsync(null, ct))
        {
            string? externalId = vm.ExternalId;
            if (vm.CurrentExistence != CurrentExistence.Exists || externalId == null)
            {
                continue;
            }
            await commands.EnqueueVmBackupAsync(vm.Id, externalId, TenantAggregate.ProjectNameFor(vm.TenantId),
                vm.TenantId, vm.ClusterId, today, ct);
        }

        foreach (var volume in await volumes.ListBillableAsync(ct))
        {
            string? externalId = volume.ExternalId;
            if (volume.CurrentExistence != CurrentExistence.Exists || externalId == null)
            {
                continue;
            }
            await commands.EnqueueVolumeBackupAsync(volume.Id, externalId, TenantAggregate.ProjectNameFor(volume.TenantId),
                volume.TenantId, volume.ClusterId, today, ct);
        }

        // One DbContext carries every repository: this saves the commands of both loops.
        await vms.SaveAsync(ct);
    }
}

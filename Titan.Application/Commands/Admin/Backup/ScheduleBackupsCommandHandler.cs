using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Admin.Backup;

/// Asks the worker for today's backup of every VM and custom volume Incus holds, across tenants
/// (titan-ops features/backup/design.md#incus-backup). Runs every hour: the
/// day is in the command's key, so a day's backup that ran is not asked again and one that failed is. The commands are
/// saved once, at the end.
public class ScheduleBackupsCommandHandler(
    IVmRepository vms,
    IVolumeRepository volumes,
    IProviderCommandQueue commands,
    IJobPassLock passLock,
    TimeProvider clock)
{
    public const string Job = "backup-schedule";

    public async Task HandleAsync(ScheduleBackupsCommand command, CancellationToken ct = default)
    {
        // Another pod's pass of this job waits here until this one saved, then reads the commands it saved: without the
        // lock two passes insert the same keys in different orders and deadlock.
        await passLock.TakeAsync(Job, ct);
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        foreach (var vm in await vms.ListWithAnInstanceAsync(ct))
        {
            await commands.EnqueueVmBackupAsync(vm, vm.GetExternalId(), TenantAggregate.ProjectNameFor(vm.TenantId), today, ct);
        }

        // Billed is what Incus holds (Volume.IsBilled): the volumes a backup can export.
        foreach (var volume in await volumes.ListBillableAsync(ct))
        {
            string? externalId = volume.ExternalId;
            if (externalId == null)
            {
                continue;
            }
            await commands.EnqueueVolumeBackupAsync(volume, externalId, TenantAggregate.ProjectNameFor(volume.TenantId), today, ct);
        }

        // One DbContext carries every repository: this saves the commands of both loops.
        await vms.SaveAsync(ct);
    }
}

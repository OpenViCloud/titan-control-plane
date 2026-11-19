using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Admin.Backup;

/// Asks the worker for today's backup of every VM and custom volume Incus holds, across tenants
/// (titan-ops features/backup/design.md#incus-backup), and for today's config of each cluster once that cluster's disks
/// of the day have ended (titan-ops features/backup/design.md#cluster-backup). Runs every hour: the day is in the
/// command's key, so a day's backup that ran is not asked again and one that failed is. The commands are saved once, at
/// the end.
public class ScheduleBackupsCommandHandler(
    IVmRepository vms,
    IVolumeRepository volumes,
    IProviderCommandQueue commands,
    IProviderCommandRepository rows,
    IJobPassLock passLock,
    TimeProvider clock,
    ClusterOptions clusters)
{
    public const string Job = "backup-schedule";

    public async Task HandleAsync(ScheduleBackupsCommand command, CancellationToken ct = default)
    {
        // Another pod's pass of this job waits here until this one saved, then reads the commands it saved: without the
        // lock two passes insert the same keys in different orders and deadlock.
        await passLock.TakeAsync(Job, ct);
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var vmsOnIncus = await vms.ListWithAnInstanceAsync(ct);
        // Billed is what Incus holds (Volume.IsBilled): the volumes a backup can export.
        var volumesOnIncus = new List<(VolumeAggregate Volume, string ExternalId)>();
        foreach (var volume in await volumes.ListBillableAsync(ct))
        {
            string? externalId = volume.ExternalId;
            if (externalId == null)
            {
                continue;
            }
            volumesOnIncus.Add((volume, externalId));
        }

        // Read before this pass asks again: a disk backup that failed has ended for the config, even though the pass
        // below runs it again.
        var disksOfTheDay = vmsOnIncus.Select(vm => (vm.ClusterId, Key: BackupCommandKeys.Vm(vm.Id, today)))
            .Concat(volumesOnIncus.Select(v => (v.Volume.ClusterId, Key: BackupCommandKeys.Volume(v.Volume.Id, today))))
            .ToList();
        var clustersDone = new List<string>();
        foreach (ClusterConfig cluster in clusters.Clusters)
        {
            if (await DisksEndedAsync(disksOfTheDay.Where(d => d.ClusterId == cluster.Id).Select(d => d.Key), ct))
            {
                clustersDone.Add(cluster.Id);
            }
        }

        foreach (var vm in vmsOnIncus)
        {
            await commands.EnqueueVmBackupAsync(vm, vm.GetExternalId(), TenantAggregate.ProjectNameFor(vm.TenantId), today, ct);
        }
        foreach ((VolumeAggregate volume, string externalId) in volumesOnIncus)
        {
            await commands.EnqueueVolumeBackupAsync(volume, externalId, TenantAggregate.ProjectNameFor(volume.TenantId), today, ct);
        }
        foreach (string clusterId in clustersDone)
        {
            await commands.EnqueueClusterBackupAsync(clusterId, today, ct);
        }

        // One DbContext carries every repository: this saves the commands of both loops.
        await vms.SaveAsync(ct);
    }

    // Every disk backup of the day has a row that is Done or Failed; a cluster with no disk has nothing to wait for.
    private async Task<bool> DisksEndedAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (string key in keys)
        {
            ProviderCommandAggregate? row = await rows.FindByKeyAsync(key, ct);
            bool ended = row != null && (row.Status == ProviderCommandStatus.Done || row.Status == ProviderCommandStatus.Failed);
            if (!ended)
            {
                return false;
            }
        }
        return true;
    }
}

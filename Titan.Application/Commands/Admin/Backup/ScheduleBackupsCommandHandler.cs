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
/// of the day have ended, and again later in the day when a disk's backup row came after the newest config (titan-ops
/// features/backup/design.md#cluster-backup). Runs every hour: the day is in the
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
        DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);

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
        DateTimeOffset dayStart = new(today.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var clustersDone = new List<string>();
        var clustersAskedAgain = new List<string>();
        foreach (ClusterConfig cluster in clusters.Clusters)
        {
            (bool ended, DateTimeOffset newestDisk) =
                await DisksOfTheDayAsync(disksOfTheDay.Where(d => d.ClusterId == cluster.Id).Select(d => d.Key), ct);
            if (!ended)
            {
                continue;
            }
            clustersDone.Add(cluster.Id);
            if (await ADiskIsNewerThanTheLastConfigAsync(cluster.Id, newestDisk, dayStart, ct))
            {
                clustersAskedAgain.Add(cluster.Id);
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
        foreach (string clusterId in clustersAskedAgain)
        {
            await commands.EnqueueClusterBackupAgainAsync(clusterId, today, now.Hour, ct);
        }

        // One DbContext carries every repository: this saves the commands of both loops.
        await vms.SaveAsync(ct);
    }

    // Every disk backup of the day has a row that is Done or Failed; a cluster with no disk has nothing to wait for. The
    // newest creation time among the rows says whether a disk came after a config: a row is made by the first pass that
    // sees its disk and is not made again that day, a failed one reopened by a later pass keeps it.
    private async Task<(bool Ended, DateTimeOffset NewestCreatedAt)> DisksOfTheDayAsync(IEnumerable<string> keys, CancellationToken ct)
    {
        DateTimeOffset newest = DateTimeOffset.MinValue;
        foreach (string key in keys)
        {
            ProviderCommandAggregate? row = await rows.FindByKeyAsync(key, ct);
            bool ended = row != null && (row.Status == ProviderCommandStatus.Done || row.Status == ProviderCommandStatus.Failed);
            if (!ended)
            {
                return (false, newest);
            }
            if (row!.CreatedAt > newest)
            {
                newest = row.CreatedAt;
            }
        }
        return (true, newest);
    }

    // The day's first config is asked on every pass of a cluster whose disks ended. Another one is asked when no config of the day is still
    // running and a disk's row is newer than the newest config that is Done: the config does not know that disk, and
    // restoring from it would leave the disk out until tomorrow's.
    private async Task<bool> ADiskIsNewerThanTheLastConfigAsync(string clusterId, DateTimeOffset newestDisk, DateTimeOffset dayStart, CancellationToken ct)
    {
        var configs = await rows.ListOfKindSinceAsync(ProviderCommandKinds.ClusterBackup, clusterId, dayStart, ct);
        if (configs.Any(c => c.Status != ProviderCommandStatus.Done && c.Status != ProviderCommandStatus.Failed))
        {
            return false;
        }
        ProviderCommandAggregate? lastDone = configs.Where(c => c.Status == ProviderCommandStatus.Done).MaxBy(c => c.CreatedAt);
        return lastDone != null && newestDisk > lastDone.CreatedAt;
    }
}

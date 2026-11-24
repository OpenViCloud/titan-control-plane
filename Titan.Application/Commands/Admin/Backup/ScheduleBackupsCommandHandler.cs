using System.Text.Json;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Application.Commands.Admin.Backup;

/// Asks the worker for today's backup of every VM and custom volume Incus holds, across tenants
/// (titan-ops features/backup/design.md#incus-backup), and for today's config of each cluster once that cluster's disks
/// of the day have ended, and again later in the day when no config of the day is still running and a disk's backup row
/// came after the newest Done config (titan-ops features/backup/design.md#cluster-backup). Runs every hour: the day is in
/// the command's key (a config asked again has the hour after the day), so a backup that ran is not asked again within
/// its day, or its hour, and one that failed is. Once a cluster's disks have ended it also asks the day's restore drill
/// of one disk whose backup is Done, once per day whatever its outcome (titan-ops features/backup/design.md#restore-drill).
/// The commands are saved once, at the end.
public class ScheduleBackupsCommandHandler(
    IVmRepository vms,
    IVolumeRepository volumes,
    IProviderCommandQueue commands,
    IProviderCommandRepository rows,
    IJobPassLock passLock,
    TimeProvider clock,
    ClusterOptions clusters,
    RestoreDrillOptions? restoreDrill = null)
{
    public const string Job = "backup-schedule";

    // A provider command row is deleted after this long (the retention job), so "drilled in the last 30 days" is the rows there are.
    private static readonly TimeSpan DrillMemory = TimeSpan.FromDays(30);

    public async Task HandleAsync(ScheduleBackupsCommand command, CancellationToken ct = default)
    {
        // Another pod's pass of this job waits here until this one saved, then reads the commands it saved: without the
        // lock two passes insert the same keys in different orders and deadlock.
        await passLock.TakeAsync(Job, ct);
        DateTimeOffset now = clock.GetUtcNow().ToUniversalTime();
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);

        var vmsOnIncus = await vms.ListWithAnInstanceAsync(ct);
        // What Incus holds, billed or not (Volume.IsOnIncus): the volumes a backup can export. Not the billed ones: a
        // volume restored from a backup is free and has a disk to lose like any other.
        var volumesOnIncus = new List<(VolumeAggregate Volume, string ExternalId)>();
        foreach (var volume in await volumes.ListOnIncusAsync(ct))
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
        if (restoreDrill is { Enabled: true })
        {
            foreach (string clusterId in clustersDone)
            {
                await AskRestoreDrillAsync(clusterId, today, now, vmsOnIncus, volumesOnIncus, ct);
            }
        }

        // One DbContext carries every repository: this saves the commands of both loops.
        await vms.SaveAsync(ct);
    }

    // One drill a day per cluster, asked once whatever its outcome: a Failed one is not reopened, unlike a disk backup, because it
    // is heavy and its failure is for the Backups page to show. The disk is the one drilled the longest ago (never first),
    // volumes before VMs among equals, from the cluster's disks whose backup of today is Done: only they have an archive of today.
    private async Task AskRestoreDrillAsync(
        string clusterId, DateOnly today, DateTimeOffset now,
        IEnumerable<VmAggregate> vmsOnIncus, IEnumerable<(VolumeAggregate Volume, string ExternalId)> volumesOnIncus, CancellationToken ct)
    {
        if (await rows.FindByKeyAsync(BackupCommandKeys.RestoreDrill(clusterId, today), ct) != null)
        {
            return;
        }
        var candidates = new List<(string Kind, Guid Id, string ExternalId, string Name, Guid TenantId)>();
        foreach (var volume in volumesOnIncus.Where(v => v.Volume.ClusterId == clusterId))
        {
            if (await BackupIsDoneAsync(BackupCommandKeys.Volume(volume.Volume.Id, today), ct))
            {
                candidates.Add(("volume", volume.Volume.Id, volume.ExternalId, volume.Volume.Name, volume.Volume.TenantId));
            }
        }
        foreach (var vm in vmsOnIncus.Where(v => v.ClusterId == clusterId))
        {
            if (await BackupIsDoneAsync(BackupCommandKeys.Vm(vm.Id, today), ct))
            {
                candidates.Add(("vm", vm.Id, vm.GetExternalId(), vm.Name, vm.TenantId));
            }
        }
        if (candidates.Count == 0)
        {
            return;
        }
        var lastDrilled = await LastDrilledAsync(clusterId, now, ct);
        var chosen = candidates
            .OrderBy(c => lastDrilled.GetValueOrDefault(c.Id, DateTimeOffset.MinValue))
            .ThenBy(c => c.Kind == "volume" ? 0 : 1)
            .ThenBy(c => c.Id)
            .First();
        await commands.EnqueueRestoreDrillAsync(clusterId, today, chosen.Kind, chosen.Id, chosen.ExternalId, chosen.Name,
            TenantAggregate.ProjectNameFor(chosen.TenantId), chosen.TenantId, ct);
    }

    private async Task<bool> BackupIsDoneAsync(string key, CancellationToken ct) =>
        (await rows.FindByKeyAsync(key, ct))?.Status == ProviderCommandStatus.Done;

    // When each disk was last asked to drill, whatever came of it: a disk that fails its drill every time must not take every day.
    private async Task<Dictionary<Guid, DateTimeOffset>> LastDrilledAsync(string clusterId, DateTimeOffset now, CancellationToken ct)
    {
        var last = new Dictionary<Guid, DateTimeOffset>();
        foreach (var row in await rows.ListOfKindSinceAsync(ProviderCommandKinds.RestoreDrill, clusterId, now - DrillMemory, ct))
        {
            try
            {
                using JsonDocument payload = JsonDocument.Parse(row.Payload);
                Guid id = payload.RootElement.GetProperty("resourceId").GetGuid();
                if (!last.TryGetValue(id, out DateTimeOffset seen) || row.CreatedAt > seen)
                {
                    last[id] = row.CreatedAt;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A row whose payload this reader does not know names no disk.
            }
        }
        return last;
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

    // The day's first config is asked on every pass of a cluster whose disks ended. Another one is asked when no config
    // of the day is still running and a disk's row is newer than the newest config that is Done: the config does not know
    // that disk, and restoring from it would leave the disk out until tomorrow's.
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

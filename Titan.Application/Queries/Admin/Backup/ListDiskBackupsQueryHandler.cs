using Titan.Application.Options;
using Titan.Application.Services.Platform.Backup;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.BackupObservation;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Admin.Backup;

/// The newest backup and health of every VM and custom volume Incus holds, across tenants, from the cache the refresh keeps
/// (titan-ops features/backup/design.md#incus-backup), and of every configured cluster's config
/// (titan-ops features/backup/design.md#cluster-backup). Listed from the resources, not from the cache: a
/// resource with no backup at all is the one to warn about. A VM or volume also lists every Complete archive of its own:
/// what an admin restores it from (titan-ops features/backup/design.md#restore-as-new-resource).
public class ListDiskBackupsQueryHandler(
    IVmRepository vms,
    IVolumeRepository volumes,
    IBackupRecordRepository records,
    IBackupObservationRepository observations,
    TimeProvider clock,
    ClusterOptions clusters)
{
    public async Task<IReadOnlyList<BackupItemQueryResult>> HandleAsync(ListDiskBackupsQuery query, CancellationToken ct = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        var observation = await observations.FindAsync(BackupLayout.IncusObservationKind, BackupLayout.IncusObservationName, ct);
        // One read of the cache for every item, grouped here: each resource keeps at most a week of archives.
        ILookup<(string Kind, string ResourceName), BackupRecord> rows = (await records.ListOfKindsAsync(
                new[] { BackupRecord.VmKind, BackupRecord.VolumeKind, BackupRecord.ClusterKind }, ct))
            .ToLookup(r => (r.Kind, r.ResourceName));

        var items = new List<BackupItemQueryResult>();
        foreach (var vm in await vms.ListAllAsync(null, ct))
        {
            if (!vm.HasAnInstance)
            {
                continue;
            }
            items.Add(Item(BackupRecord.VmKind, vm.Id.ToString(), vm.Name, vm.TenantId, vm.Id, observation, rows, now));
        }
        // What Incus holds, billed or not (Volume.IsOnIncus): a volume restored from a backup is free and is listed too.
        foreach (var volume in await volumes.ListOnIncusAsync(ct))
        {
            if (!volume.IsOnIncus || volume.ExternalId == null)
            {
                continue;
            }
            items.Add(Item(BackupRecord.VolumeKind, volume.Id.ToString(), volume.Name, volume.TenantId, volume.Id, observation, rows, now));
        }
        // A configured cluster is an item whether or not it has a config yet: the one with none is the one to warn about.
        foreach (ClusterConfig cluster in clusters.Clusters)
        {
            items.Add(Item(BackupRecord.ClusterKind, cluster.Id, cluster.Name, null, null, observation, rows, now));
        }
        return items;
    }

    // resourceId: the Titan id of a VM or volume, which an admin restores from its archives; a cluster's config is
    // restored with the whole cluster, so it has neither.
    private static BackupItemQueryResult Item(
        string kind, string resourceName, string name, Guid? tenantId, Guid? resourceId,
        BackupObservation? observation, ILookup<(string Kind, string ResourceName), BackupRecord> rows, DateTimeOffset now)
    {
        List<BackupRecord> own = rows[(kind, resourceName)].OrderByDescending(r => r.TakenAt).ToList();
        BackupRecord? newest = own.FirstOrDefault();
        var (health, reason) = BackupHealthPolicy.ForDisk(observation, newest, now);
        DateTimeOffset? takenAt = null;
        long? sizeBytes = null;
        if (newest != null)
        {
            takenAt = newest.TakenAt;
            sizeBytes = newest.SizeBytes;
        }
        var archives = new List<BackupArchiveQueryResult>();
        if (resourceId != null)
        {
            foreach (BackupRecord archive in own.Where(r => r.IsComplete))
            {
                archives.Add(new BackupArchiveQueryResult(archive.BackupKey, archive.TakenAt, archive.SizeBytes));
            }
        }
        return new BackupItemQueryResult(kind, name, tenantId, takenAt, sizeBytes, health, reason, null, resourceId, archives);
    }
}

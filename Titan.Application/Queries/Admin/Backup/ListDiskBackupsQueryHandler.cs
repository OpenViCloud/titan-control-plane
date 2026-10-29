using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Admin.Backup;

/// The newest backup and health of every VM and custom volume Incus holds, across tenants, from the cache the refresh keeps
/// (titan-ops features/backup/design.md#incus-backup). Listed from the resources, not from the cache: a
/// resource with no backup at all is the one to warn about.
public class ListDiskBackupsQueryHandler(
    IVmRepository vms,
    IVolumeRepository volumes,
    IBackupRecordRepository records,
    IBackupObservationRepository observations,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<BackupItemQueryResult>> HandleAsync(ListDiskBackupsQuery query, CancellationToken ct = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        var observation = await observations.FindAsync(BackupLayout.IncusObservationKind, BackupLayout.IncusObservationName, ct);
        List<BackupRecord> rows = await records.ListOfKindsAsync(new[] { BackupRecord.VmKind, BackupRecord.VolumeKind }, ct);

        var items = new List<BackupItemQueryResult>();
        foreach (var vm in await vms.ListAllAsync(null, ct))
        {
            if (vm.CurrentExistence != CurrentExistence.Exists || vm.ExternalId == null)
            {
                continue;
            }
            items.Add(Item(BackupRecord.VmKind, vm.Id, vm.Name, vm.TenantId, observation, rows, now));
        }
        foreach (var volume in await volumes.ListBillableAsync(ct))
        {
            if (volume.CurrentExistence != CurrentExistence.Exists || volume.ExternalId == null)
            {
                continue;
            }
            items.Add(Item(BackupRecord.VolumeKind, volume.Id, volume.Name, volume.TenantId, observation, rows, now));
        }
        return items;
    }

    private static BackupItemQueryResult Item(
        string kind, Guid id, string name, Guid tenantId,
        BackupObservation? observation, List<BackupRecord> rows, DateTimeOffset now)
    {
        string resourceName = id.ToString();
        BackupRecord? newest = rows
            .Where(r => r.Kind == kind && r.ResourceName == resourceName)
            .OrderByDescending(r => r.TakenAt)
            .FirstOrDefault();
        var (health, reason) = BackupHealthPolicy.ForDisk(observation, newest, now);
        DateTimeOffset? takenAt = null;
        long? sizeBytes = null;
        if (newest != null)
        {
            takenAt = newest.TakenAt;
            sizeBytes = newest.SizeBytes;
        }
        return new BackupItemQueryResult(kind, name, tenantId, takenAt, sizeBytes, health, reason, null);
    }
}

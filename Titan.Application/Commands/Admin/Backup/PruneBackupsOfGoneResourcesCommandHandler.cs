using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Platform.Backup;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Admin.Backup;

/// Deletes from the bucket every backup of a VM or volume Titan no longer holds (Deleted, or no row) once its newest backup
/// is past BackupLayout.GoneResourceBackupsKeptFor (titan-ops features/backup/design.md#backups-of-gone-resources). The
/// schedule stops at the delete, so the newest backup's age is how long ago the resource went, near enough. Here, in Admin,
/// because only Admin reads both the bucket's module and Compute; the refresh drops the rows of what is gone at its next pass.
public class PruneBackupsOfGoneResourcesCommandHandler(
    IOptions<BackupTargetOptions> targetOptions,
    IBackupBucketFactory buckets,
    IVmRepository vms,
    IVolumeRepository volumes,
    TimeProvider clock)
{
    public async Task HandleAsync(PruneBackupsOfGoneResourcesCommand command, CancellationToken ct = default)
    {
        // No key pair: an environment without a backup bucket. Nothing to read.
        if (!targetOptions.Value.HasKeyPair)
        {
            return;
        }
        DateTimeOffset cutoff = clock.GetUtcNow() - BackupLayout.GoneResourceBackupsKeptFor;

        using IBackupBucket bucket = buckets.Open(targetOptions.Value.ToConnection());
        IReadOnlyList<BackupBucketObject> all = await bucket.ListObjectsAsync(BackupLayout.IncusRoot, null, ct);
        var byResource = all
            .Select(o => (Object: o, Key: IncusBackupKey.Parse(o.Key)))
            .Where(b => b.Key is not null)
            .GroupBy(b => (b.Key!.Kind, b.Key.ResourceId))
            .ToList();

        var vmIds = byResource.Where(g => g.Key.Kind == BackupRecord.VmKind).Select(g => g.Key.ResourceId).ToList();
        var volumeIds = byResource.Where(g => g.Key.Kind == BackupRecord.VolumeKind).Select(g => g.Key.ResourceId).ToList();
        List<Guid> vmsHeld = await vms.ListIdsNotDeletedAsync(vmIds, ct);
        List<Guid> volumesHeld = await volumes.ListIdsNotDeletedAsync(volumeIds, ct);

        foreach (var resource in byResource)
        {
            bool held = resource.Key.Kind == BackupRecord.VmKind
                ? vmsHeld.Contains(resource.Key.ResourceId)
                : volumesHeld.Contains(resource.Key.ResourceId);
            DateTimeOffset newest = resource.Max(b => b.Key!.TakenAt);
            if (held || newest >= cutoff)
            {
                continue;
            }
            foreach (var backup in resource)
            {
                await bucket.DeleteAsync(backup.Object.Key, ct);
            }
        }
    }
}

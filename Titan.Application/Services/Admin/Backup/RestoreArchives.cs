using Titan.Domain.Aggregates.BackupRecord;

namespace Titan.Application.Services.Admin.Backup;

/// Which of a VM's or a volume's archives a restore reads (titan-ops features/backup/design.md#restore-as-new-resource).
/// Pure: the caller reads the resource's rows from the backup cache and hands them in. Only a Complete archive is one
/// to restore from.
public static class RestoreArchives
{
    /// A VM's archive holds its root disk only, so each volume attached to it is restored from an archive of its own.
    /// Further apart than this, the two disks are of two different moments of the VM.
    public static readonly TimeSpan GoesWithItsVmWithin = TimeSpan.FromHours(24);

    /// The archive the admin named, or null.
    public static BackupRecord? WithKey(IEnumerable<BackupRecord> archives, string backupKey)
    {
        foreach (BackupRecord archive in archives)
        {
            if (archive.IsComplete && archive.BackupKey == backupKey)
            {
                return archive;
            }
        }
        return null;
    }

    /// The archive that goes with a VM's archive taken at vmArchiveTakenAt: the one taken closest to it, before or after,
    /// the earlier of two equally far. Null when there is none, or when the closest is further than GoesWithItsVmWithin:
    /// one further away still is not looked for.
    public static BackupRecord? ClosestTo(IEnumerable<BackupRecord> archives, DateTimeOffset vmArchiveTakenAt)
    {
        BackupRecord? closest = null;
        TimeSpan closestDistance = TimeSpan.MaxValue;
        foreach (BackupRecord archive in archives)
        {
            if (!archive.IsComplete)
            {
                continue;
            }
            TimeSpan distance = (archive.TakenAt - vmArchiveTakenAt).Duration();
            bool isCloser = distance < closestDistance;
            bool isAsCloseAndEarlier = closest != null && distance == closestDistance && archive.TakenAt < closest.TakenAt;
            if (isCloser || isAsCloseAndEarlier)
            {
                closest = archive;
                closestDistance = distance;
            }
        }
        if (closest == null || closestDistance > GoesWithItsVmWithin)
        {
            return null;
        }
        return closest;
    }
}

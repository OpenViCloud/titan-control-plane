using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.BackupObservation;
using Titan.Domain.Aggregates.BackupRecord;

namespace Titan.Application.Queries.Platform.Backup;

// titan-ops features/backup/design.md#backup-status-reads-the-bucket. Postgres is Stale by the age of its newest complete base backup only; the age of the
// newest log is shown but never alarms, because an idle database writes no log and cannot be told from a broken archiver.
public static class BackupHealthPolicy
{
    /// A daily ScheduledBackup plus two hours of slack.
    public static readonly TimeSpan PostgresBaseStaleAfter = TimeSpan.FromHours(26);

    /// The refresh runs every few minutes; a reading older than this means the refresh itself has stopped.
    public static readonly TimeSpan ReadingMaxAge = TimeSpan.FromMinutes(30);

    public const string NotReadYet = "NotReadYet";
    public const string BucketUnreadable = "BucketUnreadable";
    public const string StatusTooOld = "StatusTooOld";
    public const string NoBackup = "NoBackup";
    public const string NoPostgresBackupInBucket = "NoPostgresBackupInBucket";
    public const string OnlyIncompleteBackups = "OnlyIncompleteBackups";
    public const string BaseTooOld = "BaseTooOld";
    public const string BackupTooOld = "BackupTooOld";

    /// A VM or volume is backed up once a day (titan-ops features/backup/design.md#incus-backup), plus two hours of slack.
    public static readonly TimeSpan DiskStaleAfter = TimeSpan.FromHours(26);

    public static (BackupHealth Health, string? Reason) ForPostgres(
        BackupObservation? observation, IReadOnlyList<BackupRecord> records, DateTimeOffset now)
    {
        if (observation is null)
        {
            return (BackupHealth.Unknown, NotReadYet);
        }
        if (observation.Outcome == BackupReadOutcome.Unreadable)
        {
            return (BackupHealth.Unknown, BucketUnreadable);
        }
        if (now - observation.LastReadAt > ReadingMaxAge)
        {
            return (BackupHealth.Unknown, StatusTooOld);
        }
        if (observation.Outcome == BackupReadOutcome.Empty)
        {
            return (BackupHealth.Missing, NoBackup);
        }
        if (observation.Outcome == BackupReadOutcome.NoBackupPrefix)
        {
            return (BackupHealth.Missing, NoPostgresBackupInBucket);
        }

        BackupRecord? newest = records
            .Where(r => r.Status == BackupRecordStatus.Complete)
            .OrderByDescending(r => r.TakenAt)
            .FirstOrDefault();
        if (newest is null)
        {
            bool anyIncomplete = records.Any(r => r.Status == BackupRecordStatus.Incomplete);
            return (BackupHealth.Missing, anyIncomplete ? OnlyIncompleteBackups : NoBackup);
        }
        if (now - newest.TakenAt > PostgresBaseStaleAfter)
        {
            return (BackupHealth.Stale, BaseTooOld);
        }
        return (BackupHealth.Ok, null);
    }

    /// A VM's or volume's health from the one incus/ observation and the resource's newest backup. Nothing found for a
    /// resource that Incus holds is Missing; a bucket that could not be read is Unknown, whatever the cache still holds.
    public static (BackupHealth Health, string? Reason) ForDisk(
        BackupObservation? observation, BackupRecord? newest, DateTimeOffset now)
    {
        if (observation is null)
        {
            return (BackupHealth.Unknown, NotReadYet);
        }
        if (observation.Outcome == BackupReadOutcome.Unreadable)
        {
            return (BackupHealth.Unknown, BucketUnreadable);
        }
        if (now - observation.LastReadAt > ReadingMaxAge)
        {
            return (BackupHealth.Unknown, StatusTooOld);
        }
        if (newest is null)
        {
            return (BackupHealth.Missing, NoBackup);
        }
        if (now - newest.TakenAt > DiskStaleAfter)
        {
            return (BackupHealth.Stale, BackupTooOld);
        }
        return (BackupHealth.Ok, null);
    }
}

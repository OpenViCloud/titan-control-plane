namespace Titan.Domain.Aggregates.BackupObservation;

public enum BackupReadOutcome
{
    /// The bucket was read and a serverName under postgres/ has objects.
    Read,
    /// The bucket was read and holds nothing at all.
    Empty,
    /// The bucket was read, holds other objects, and no serverName under postgres/: usually the wrong bucket.
    NoBackupPrefix,
    /// The last attempt to read the bucket failed; what was known before is kept.
    Unreadable,
}

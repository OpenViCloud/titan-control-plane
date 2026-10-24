namespace Titan.Application.Queries.Platform.Backup.Contracts;

public enum BackupHealth
{
    Ok,
    Stale,
    Missing,
    /// The bucket could not be read (or has not been read yet): not the same as having no backup.
    Unknown,
}

namespace Titan.Application.Commands.Platform.Backup;

// Where barman (Barman Cloud Plugin) puts Postgres backups in the bucket
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-status-reads-the-bucket); measured on the dev bucket:
// postgres/<serverName>/base/<id>/backup.info and data.tar, and postgres/<serverName>/wals/. The serverName changes with each restore,
// so the code reads the one that wrote last; the page keeps one resource, titan-db, whatever name it archives under.
public static class BackupLayout
{
    public const string PostgresResourceName = "titan-db";
    public const string PostgresRoot = "postgres/";
    public const string BaseFolder = "base/";
    public const string WalFolder = "wals/";
    public const string BackupInfoFile = "backup.info";

    /// Where Incus uploads VM and volume backups (IncusBackupKey).
    public const string IncusRoot = "incus/";

    /// The one observation of the incus/ pass: whether the bucket could be read, for every VM and volume at once.
    public const string IncusObservationKind = "incus";
    public const string IncusObservationName = "disks";

    /// Backups kept per VM or volume; older ones are deleted from the bucket.
    public const int IncusBackupsKept = 7;

    /// The serverName an object of postgres/ belongs to, or null for an object right under postgres/.
    public static string? ServerOf(string key)
    {
        int slash = key.IndexOf('/', PostgresRoot.Length);
        return slash > PostgresRoot.Length ? key[PostgresRoot.Length..slash] : null;
    }
}

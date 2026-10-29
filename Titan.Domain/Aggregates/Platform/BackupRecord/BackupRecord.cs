namespace Titan.Domain.Aggregates.Platform.BackupRecord;

// One backup seen in the bucket: a Postgres base backup, or a VM's or a volume's archive
// (titan-ops features/backup/design.md#backup-status-reads-the-bucket).
// A cache for the admin page: the bucket is the truth, and a refresh rebuilds every row from it.
public class BackupRecord
{
    public const string PostgresKind = "postgres";
    public const string VmKind = "vm";
    public const string VolumeKind = "volume";
    public const int MaxBackupKeyLength = 128;

    /// A barman backup folder name fits the column; an empty or overlong one is not a backup this cache can hold.
    public static bool IsValidBackupKey(string? backupKey) =>
        !string.IsNullOrWhiteSpace(backupKey) && backupKey.Length <= MaxBackupKeyLength;

    public Guid Id { get; private set; }
    public string Kind { get; private set; } = default!;
    /// titan-db for Postgres; the VM's or volume's Titan id for theirs.
    public string ResourceName { get; private set; } = default!;
    /// The tenant a VM or volume backup belongs to; null for Postgres, which is the platform's.
    public Guid? TenantId { get; private set; }
    /// The folder name barman gave the backup, e.g. 20261024T140631; for a VM or volume, the time in its object key.
    public string BackupKey { get; private set; } = default!;
    public DateTimeOffset TakenAt { get; private set; }
    public long SizeBytes { get; private set; }
    public BackupRecordStatus Status { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }

    private BackupRecord() { }

    public static BackupRecord Create(
        string kind, string resourceName, string backupKey, DateTimeOffset takenAt, long sizeBytes,
        BackupRecordStatus status, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(resourceName) || string.IsNullOrWhiteSpace(backupKey))
        {
            throw new ArgumentException("kind, resourceName and backupKey must not be empty", nameof(kind));
        }
        if (sizeBytes < 0)
        {
            throw new ArgumentException("sizeBytes must not be negative", nameof(sizeBytes));
        }
        return new BackupRecord
        {
            Id = Guid.NewGuid(), Kind = kind, ResourceName = resourceName, BackupKey = backupKey,
            TakenAt = takenAt, SizeBytes = sizeBytes, Status = status, ObservedAt = observedAt,
        };
    }

    /// A VM's or volume's archive: always complete, since the bucket shows an upload only once it has finished.
    public static BackupRecord CreateForTenant(
        string kind, string resourceName, Guid tenantId, string backupKey, DateTimeOffset takenAt, long sizeBytes,
        DateTimeOffset observedAt)
    {
        BackupRecord record = Create(kind, resourceName, backupKey, takenAt, sizeBytes, BackupRecordStatus.Complete, observedAt);
        record.TenantId = tenantId;
        return record;
    }

    public void Observe(DateTimeOffset takenAt, long sizeBytes, BackupRecordStatus status, DateTimeOffset observedAt)
    {
        if (sizeBytes < 0)
        {
            throw new ArgumentException("sizeBytes must not be negative", nameof(sizeBytes));
        }
        TakenAt = takenAt;
        SizeBytes = sizeBytes;
        Status = status;
        ObservedAt = observedAt;
    }
}

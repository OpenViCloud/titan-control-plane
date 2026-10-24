namespace Titan.Domain.Aggregates.Platform.BackupRecord;

// One base backup seen in the bucket (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-status-reads-the-bucket).
// A cache for the admin page: the bucket is the truth, and a refresh rebuilds every row from it.
public class BackupRecord
{
    public const string PostgresKind = "postgres";
    public const int MaxBackupKeyLength = 128;

    public Guid Id { get; private set; }
    public string Kind { get; private set; } = default!;
    public string ResourceName { get; private set; } = default!;
    /// The folder name barman gave the backup, e.g. 20261024T140631.
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

namespace Titan.Domain.Aggregates.Platform.BackupObservation;

// What the last pass over the bucket saw for one protected resource, and whether the pass could read it at all
// (titan-ops features/backup/design.md#backup-status-reads-the-bucket). "Could not read" is its own outcome so the page never turns it into "no backup".
public class BackupObservation
{
    public const int MaxErrorLength = 200;

    public string Kind { get; private set; } = default!;
    public string ResourceName { get; private set; } = default!;
    public BackupReadOutcome Outcome { get; private set; }
    /// Newest log object under the resource's wals/ prefix (its LastModified). Kept across an unreadable pass.
    public DateTimeOffset? LastWalAt { get; private set; }
    public DateTimeOffset LastReadAt { get; private set; }
    public string? Error { get; private set; }

    private BackupObservation() { }

    public static BackupObservation Create(string kind, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(resourceName))
        {
            throw new ArgumentException("kind and resourceName must not be empty", nameof(kind));
        }
        return new BackupObservation { Kind = kind, ResourceName = resourceName, Outcome = BackupReadOutcome.Unreadable };
    }

    public void RecordRead(BackupReadOutcome outcome, DateTimeOffset? lastWalAt, DateTimeOffset now)
    {
        if (outcome == BackupReadOutcome.Unreadable)
        {
            throw new ArgumentException("use RecordUnreadable for a failed read", nameof(outcome));
        }
        Outcome = outcome;
        LastWalAt = lastWalAt;
        LastReadAt = now;
        Error = null;
    }

    public void RecordUnreadable(string error, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            throw new ArgumentException("error must name the failure", nameof(error));
        }
        Outcome = BackupReadOutcome.Unreadable;
        LastReadAt = now;
        Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
    }
}

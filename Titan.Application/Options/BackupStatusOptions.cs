namespace Titan.Application.Options;

public class BackupStatusOptions
{
    public const string SectionName = "BackupStatus";

    /// <summary>How often the bucket is read; the page calls a reading older than 30 minutes stale.</summary>
    public int IntervalMinutes { get; set; } = 5;
}

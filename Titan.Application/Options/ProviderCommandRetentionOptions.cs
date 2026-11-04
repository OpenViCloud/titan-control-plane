namespace Titan.Application.Options;

public class ProviderCommandRetentionOptions
{
    public const string SectionName = "ProviderCommandRetention";

    /// A finished row older than this is deleted (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    public int RetentionDays { get; set; } = 30;

    public int IntervalMinutes { get; set; } = 60;
}

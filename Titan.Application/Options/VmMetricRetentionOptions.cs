namespace Titan.Application.Options;

public class VmMetricRetentionOptions
{
    public const string SectionName = "VmMetricRetention";

    /// A sample older than this is deleted (titan-ops features/vm/design.md#vm-metrics): the longest range a tenant reads is 7d.
    public int RetentionDays { get; set; } = 7;

    public int IntervalMinutes { get; set; } = 60;
}

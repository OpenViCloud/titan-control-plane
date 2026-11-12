namespace Titan.Application.Options;

/// How often the retention pass runs. How long a record is kept is UsageRecord.KeptDays, not an option: the month rule
/// reads the same number (titan-ops features/billing/metering/design.md#usage-retention).
public class UsageRecordRetentionOptions
{
    public const string SectionName = "UsageRecordRetention";

    public int IntervalMinutes { get; set; } = 60;
}

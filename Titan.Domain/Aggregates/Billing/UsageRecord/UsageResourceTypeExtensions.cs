namespace Titan.Domain.Aggregates.Billing.UsageRecord;

/// The token must stay byte-identical to existing rows: it keys the metering idempotency index
/// (titan-ops architecture/money-and-wallet.md#append-only-metering). Explicit rather than HasConversion<string>()
/// because the token is not the member name, and shared by both EF converters and the raw-SQL insert in
/// UsageRecordRepository.TryInsertAsync (titan-ops architecture/code-structure.md#enum-storage).
public static class UsageResourceTypeExtensions
{
    public static string ToDbString(this UsageResourceType type) => type switch
    {
        UsageResourceType.Compute => "COMPUTE",
        UsageResourceType.Disk => "DISK",
        UsageResourceType.Snapshot => "SNAPSHOT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown UsageResourceType")
    };

    public static UsageResourceType FromDbString(string token) => token switch
    {
        "COMPUTE" => UsageResourceType.Compute,
        "DISK" => UsageResourceType.Disk,
        "SNAPSHOT" => UsageResourceType.Snapshot,
        _ => throw new ArgumentException($"Unknown usage resource type token '{token}'", nameof(token))
    };
}

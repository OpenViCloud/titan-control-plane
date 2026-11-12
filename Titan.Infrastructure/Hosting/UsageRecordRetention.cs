using Microsoft.Extensions.Logging;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Deletes usage records older than UsageRecord.KeptDays (titan-ops features/billing/metering/design.md#usage-retention).
/// Two passes at once delete the same rows and the later one finds none. Local DB maintenance, like VmMetricRetention,
/// not a provider command.
/// </summary>
public class UsageRecordRetention(
    IUsageRecordRepository repository,
    TimeProvider clock,
    ILogger<UsageRecordRetention> logger) : IScheduledJob
{
    public const string JobName = "usage-record-retention";

    public string Name => JobName;

    public Task RunAsync(CancellationToken ct) =>
        RunOnceAsync(repository, clock.GetUtcNow(), logger, ct);

    public static async Task<int> RunOnceAsync(
        IUsageRecordRepository repository, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        int removed = await repository.DeleteBucketBeforeAsync(UsageRecord.KeptFrom(now), ct);
        if (removed > 0)
        {
            logger.LogInformation("Usage record retention removed {Count} records older than {Days} days",
                removed, UsageRecord.KeptDays);
        }
        return removed;
    }
}

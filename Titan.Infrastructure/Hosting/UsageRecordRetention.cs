using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Deletes usage records older than UsageRecord.KeptDays (titan-ops features/billing/metering/design.md#usage-retention).
/// Every pod runs its own pass; two passes at once delete the same rows and the later one finds none. Local DB
/// maintenance, like VmMetricRetention, not a provider command.
/// </summary>
public class UsageRecordRetention(
    IServiceScopeFactory scopeFactory,
    IOptions<UsageRecordRetentionOptions> options,
    TimeProvider clock,
    ILogger<UsageRecordRetention> logger) : BackgroundService
{
    private readonly UsageRecordRetentionOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_opts.IntervalMinutes));

        // Wait-first, like VmMetricRetention: nothing runs during startup, nor under the InMemory provider of a
        // WebApplicationFactory test, which has no ExecuteDelete.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IUsageRecordRepository>(),
                    clock.GetUtcNow(), logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Usage record retention pass failed");
            }
        }
    }

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

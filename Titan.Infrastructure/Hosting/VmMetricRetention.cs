using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Deletes VM metric samples older than RetentionDays (titan-ops features/vm/design.md#vm-metrics). The worker writes one
/// row per VM per minute, so without it the table grows by 10,080 rows per VM each week. Local DB maintenance, like
/// ProviderCommandRetention, not a provider command.
/// </summary>
public class VmMetricRetention(
    IServiceScopeFactory scopeFactory,
    IOptions<VmMetricRetentionOptions> options,
    TimeProvider clock,
    ILogger<VmMetricRetention> logger) : BackgroundService
{
    private readonly VmMetricRetentionOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_opts.IntervalMinutes));

        // Wait-first, like ProviderCommandRetention: nothing runs during startup, nor under the InMemory provider of a
        // WebApplicationFactory test, which has no ExecuteDelete.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IVmMetricStore>(),
                    _opts.RetentionDays, clock.GetUtcNow(), logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "VM metric retention pass failed");
            }
        }
    }

    public static async Task<int> RunOnceAsync(
        IVmMetricStore store, int retentionDays, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        int removed = await store.DeleteSampledBeforeAsync(now.AddDays(-retentionDays), ct);
        if (removed > 0)
        {
            logger.LogInformation("VM metric retention removed {Count} samples older than {Days} days",
                removed, retentionDays);
        }
        return removed;
    }
}

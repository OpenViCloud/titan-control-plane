using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Deletes finished provider command rows older than RetentionDays, never an open one
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request). Every request writes a row of its own, so without
/// it the table grows with every click. Local DB maintenance, like IdempotencyCleanupService, not a provider command.
/// </summary>
public class ProviderCommandRetention(
    IServiceScopeFactory scopeFactory,
    IOptions<ProviderCommandRetentionOptions> options,
    TimeProvider clock,
    ILogger<ProviderCommandRetention> logger) : BackgroundService
{
    private readonly ProviderCommandRetentionOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_opts.IntervalMinutes));

        // Wait-first, like IdempotencyCleanupService: nothing runs during startup, nor under the InMemory provider of a
        // WebApplicationFactory test, which has no ExecuteDelete.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IProviderCommandRepository>(),
                    _opts.RetentionDays, clock.GetUtcNow(), logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Provider command retention pass failed");
            }
        }
    }

    public static async Task<int> RunOnceAsync(
        IProviderCommandRepository commands, int retentionDays, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        int removed = await commands.DeleteFinishedCreatedBeforeAsync(now.AddDays(-retentionDays), ct);
        if (removed > 0)
        {
            logger.LogInformation("Provider command retention removed {Count} finished rows older than {Days} days",
                removed, retentionDays);
        }
        return removed;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Commands.Platform.Backup.Contracts;
using Titan.Application.Options;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Reads the backup bucket on a timer and rewrites the backup cache (RefreshBackupStatusCommandHandler). A CP-side
/// BackgroundService, like the idempotency cleanup: local bookkeeping, not a provider command.
/// </summary>
public class BackupStatusRefreshService(
    IServiceScopeFactory scopeFactory,
    IOptions<BackupStatusOptions> options,
    ILogger<BackupStatusRefreshService> logger) : BackgroundService
{
    private const int MaxIntervalMinutes = 15;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // PeriodicTimer throws on a zero, negative or huge period, which would stop the host over one configuration value; and the
        // page calls a reading older than 30 minutes stale, so a longer interval would make it flap.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(options.Value.IntervalMinutes, 1, MaxIntervalMinutes)));

        // Wait-first, like the other hosted services: nothing runs during startup or in a WebApplicationFactory test.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<RefreshBackupStatusCommandHandler>();
                await handler.HandleAsync(new RefreshBackupStatusCommand(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup status refresh failed");
            }
        }
    }
}

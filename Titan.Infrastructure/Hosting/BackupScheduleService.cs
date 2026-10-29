using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Asks for the day's backup of every VM and volume (ScheduleBackupsCommandHandler) every hour. A CP-side BackgroundService
/// that only writes the control plane's own command table; the worker does the backups
/// (titan-ops features/backup/design.md#incus-backup).
/// </summary>
public class BackupScheduleService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackupScheduleService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Wait-first, like the other hosted services: nothing runs during startup or in a WebApplicationFactory test.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<ScheduleBackupsCommandHandler>();
                await handler.HandleAsync(new ScheduleBackupsCommand(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup schedule failed");
            }
        }
    }
}

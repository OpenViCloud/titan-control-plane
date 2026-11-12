using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Commands.Platform.Backup.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Reads the backup bucket and rewrites the backup cache (RefreshBackupStatusCommandHandler). A scheduled job, like the
/// idempotency cleanup: local bookkeeping, not a provider command. The page calls a reading older than 30 minutes stale,
/// so its CronJob runs more often than that.
/// </summary>
public class BackupStatusRefreshService(RefreshBackupStatusCommandHandler handler) : IScheduledJob
{
    public string Name => RefreshBackupStatusCommandHandler.Job;

    public Task RunAsync(CancellationToken ct) => handler.HandleAsync(new RefreshBackupStatusCommand(), ct);
}

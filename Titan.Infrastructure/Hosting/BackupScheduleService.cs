using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Asks for the day's backup of every VM and volume (ScheduleBackupsCommandHandler). A scheduled job that only writes the
/// control plane's own command table; the worker does the backups (titan-ops features/backup/design.md#incus-backup).
/// </summary>
public class BackupScheduleService(ScheduleBackupsCommandHandler handler) : IScheduledJob
{
    public string Name => ScheduleBackupsCommandHandler.Job;

    public Task RunAsync(CancellationToken ct) => handler.HandleAsync(new ScheduleBackupsCommand(), ct);
}

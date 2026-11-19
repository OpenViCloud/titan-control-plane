using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Asks for the day's backup of every VM, volume and cluster config (ScheduleBackupsCommandHandler), then deletes the
/// backups of VMs and volumes gone past their keeping time (PruneBackupsOfGoneResourcesCommandHandler). The worker does the
/// backups (titan-ops features/backup/design.md#incus-backup); the schedule is saved before the bucket is read, so a
/// bucket that cannot be read does not hold the day's commands back.
/// </summary>
public class BackupScheduleService(ScheduleBackupsCommandHandler handler, PruneBackupsOfGoneResourcesCommandHandler prune)
    : IScheduledJob
{
    public string Name => ScheduleBackupsCommandHandler.Job;

    public async Task RunAsync(CancellationToken ct)
    {
        await handler.HandleAsync(new ScheduleBackupsCommand(), ct);
        await prune.HandleAsync(new PruneBackupsOfGoneResourcesCommand(), ct);
    }
}

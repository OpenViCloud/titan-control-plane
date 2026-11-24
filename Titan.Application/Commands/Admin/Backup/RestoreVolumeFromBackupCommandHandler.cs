using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Application.Services.Admin.Backup;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Admin.Backup;

/// A platform admin restores one backup archive of a volume as a new volume of the same tenant, next to the original
/// (titan-ops features/backup/design.md#restore-as-new-resource). The new volume is the tenant's own, to see and to
/// delete, but never counts toward its quota and is never billed: no quota gate is asked, on purpose. The worker
/// imports the archive under the new volume's name and reports through the volume's create.
///
/// A cross-tenant write, so a handler of its own and not a flag on a tenant's one
/// (titan-ops architecture/tenancy-and-admin.md#no-bypass-flag): the owner is read from the source, never from the
/// caller. No activity is staged here: the recorder writes under the caller's tenant, which is the admin's own; the
/// owner's feed gets "volume.created" from the create's report.
public class RestoreVolumeFromBackupCommandHandler(
    IVolumeRepository volumes,
    IBackupRecordRepository backups,
    IAdminAuditLogRepository auditLog,
    IProviderCommandQueue commands,
    ICurrentUser currentUser,
    ILogger<RestoreVolumeFromBackupCommandHandler> logger)
{
    public async Task<RestoreVolumeFromBackupCommandResult> HandleAsync(RestoreVolumeFromBackupCommand command, CancellationToken ct = default)
    {
        ComputeInput.EnsureVolumeName(command.Name);

        // Locked, so a delete of the source and this restore take turns: the Backups page the admin pressed on is a read
        // that may be stale by now.
        VolumeAggregate? source = await volumes.FindByIdForUpdateAsync(command.VolumeId, ct);
        if (source == null || source.IsDeleted)
        {
            throw new NotFoundException("VOLUME_NOT_FOUND", "Volume not found.");
        }

        List<BackupRecord> archives = await backups.ListAsync(BackupRecord.VolumeKind, source.Id.ToString(), ct);
        BackupRecord archive = RestoreArchives.WithKey(archives, command.BackupKey)
            ?? throw new NotFoundException("BACKUP_NOT_FOUND", $"Volume '{source.Name}' has no backup '{command.BackupKey}'.");

        VolumeAggregate restored = VolumeAggregate.RestoreFromBackup(source, command.Name);
        volumes.Add(restored);
        RestoreAudit.Stage(logger, auditLog, currentUser.KeycloakUserId, RestoreAudit.VolumeAction, "volume",
            restored.Id, source.TenantId, source.Id, archive.BackupKey);

        // The restore is the new volume's create; its command's report ends it
        // (titan-ops architecture/resource-lifecycle.md#kinds). Enqueued before the save: the volume, its audit row and
        // its command commit together (titan-ops architecture/provider-and-worker.md#provider-command-table).
        await commands.EnqueueVolumeRestoreAsync(source, restored, restored.Operation.PendingId!.Value, archive.BackupKey,
            TenantAggregate.ProjectNameFor(source.TenantId), ct);
        await volumes.SaveAsync(ct);

        return new RestoreVolumeFromBackupCommandResult(restored.Id);
    }
}

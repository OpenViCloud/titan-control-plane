using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Application.Services.Admin.Backup;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Admin.Backup;

/// A platform admin restores one backup archive of a VM as a new VM of the same tenant, next to the original, with a
/// copy of every volume Incus holds attached to it (titan-ops features/backup/design.md#restore-as-new-resource). A VM's
/// archive holds its root disk only, so each volume is restored from its own archive, the one taken closest to the
/// VM's; a VM that would come back without one of its disks is not restored at all. The new rows are the tenant's own,
/// to see and to delete, but never count toward its quota and are never billed: no quota gate is asked, on purpose.
/// The worker imports the archives under the new names and reports through each row's create.
///
/// A cross-tenant write, so a handler of its own and not a flag on a tenant's one
/// (titan-ops architecture/tenancy-and-admin.md#no-bypass-flag): the owner is read from the source, never from the
/// caller. No activity is staged here: the recorder writes under the caller's tenant, which is the admin's own; the
/// owner's feed gets "vm.created" and "volume.created" from the creates' reports. No offering or template is read:
/// they are copied from the source row, which was built from them.
public class RestoreVmFromBackupCommandHandler(
    IVmRepository vms,
    IVolumeRepository volumes,
    IBackupRecordRepository backups,
    IAdminAuditLogRepository auditLog,
    IProviderCommandQueue commands,
    ICurrentUser currentUser,
    ILogger<RestoreVmFromBackupCommandHandler> logger)
{
    public async Task<RestoreVmFromBackupCommandResult> HandleAsync(RestoreVmFromBackupCommand command, CancellationToken ct = default)
    {
        ComputeInput.EnsureVmName(command.Name);

        // Locked, so a delete of the source and this restore take turns: the Backups page the admin pressed on is a read
        // that may be stale by now.
        VmAggregate? source = await vms.FindByIdForUpdateAsync(command.VmId, ct);
        if (source == null || source.IsDeleted)
        {
            throw new NotFoundException("VM_NOT_FOUND", "VM not found.");
        }

        List<BackupRecord> vmArchives = await backups.ListAsync(BackupRecord.VmKind, source.Id.ToString(), ct);
        BackupRecord vmArchive = RestoreArchives.WithKey(vmArchives, command.BackupKey)
            ?? throw new NotFoundException("BACKUP_NOT_FOUND", $"VM '{source.Name}' has no backup '{command.BackupKey}'.");

        // In the owner's tenant, as its own create checks: the unique index would answer 500.
        if (await vms.ExistsByNameAsync(source.TenantId, command.Name, ct))
        {
            throw new ConflictException("VM_NAME_TAKEN", $"VM name '{command.Name}' is already taken.");
        }

        // What goes with the VM is what Incus holds attached to it now, not what was asked onto it. Locked after the VM,
        // the one order every handler that locks both takes. Every volume is checked before anything is made.
        var attached = new List<(VolumeAggregate Volume, BackupRecord Archive)>();
        List<VolumeAggregate> held = await volumes.ListAttachedToVmLockedAsync(source.Id, ct);
        foreach (VolumeAggregate volume in held.Where(v => v.CurrentAttachedVmId == source.Id).OrderBy(v => v.CreatedAt).ThenBy(v => v.Id))
        {
            // The copy keeps its volume's name; a row that broke the rule answers as a create would.
            ComputeInput.EnsureVolumeName(volume.Name);
            List<BackupRecord> archives = await backups.ListAsync(BackupRecord.VolumeKind, volume.Id.ToString(), ct);
            BackupRecord archive = RestoreArchives.ClosestTo(archives, vmArchive.TakenAt)
                ?? throw new BusinessException("BACKUP_OF_ATTACHED_VOLUME_NOT_FOUND",
                    $"Volume '{volume.Name}' ({volume.Id}), attached to the VM, has no backup within "
                    + $"{RestoreArchives.GoesWithItsVmWithin.TotalHours:0} hours of the VM's backup '{vmArchive.BackupKey}'.");
            attached.Add((volume, archive));
        }

        // Every row is built before one is added: a source the factories refuse leaves nothing behind.
        VmAggregate restoredVm = VmAggregate.RestoreFromBackup(source, command.Name);
        var copies = new List<(VolumeAggregate Source, VolumeAggregate Copy, BackupRecord Archive)>();
        foreach ((VolumeAggregate volume, BackupRecord archive) in attached)
        {
            // Volume names are not unique, so the copy carries its original's; the new VM is the one it is asked onto.
            copies.Add((volume, VolumeAggregate.RestoreFromBackup(volume, volume.Name, restoredVm.Id), archive));
        }

        string admin = currentUser.KeycloakUserId;
        vms.Add(restoredVm);
        RestoreAudit.Stage(logger, auditLog, admin, RestoreAudit.VmAction, "vm",
            restoredVm.Id, source.TenantId, source.Id, vmArchive.BackupKey);
        foreach ((VolumeAggregate volume, VolumeAggregate copy, BackupRecord archive) in copies)
        {
            volumes.Add(copy);
            RestoreAudit.Stage(logger, auditLog, admin, RestoreAudit.VolumeAction, "volume",
                copy.Id, volume.TenantId, volume.Id, archive.BackupKey);
        }

        // Each restore is its row's create; its command's report ends it
        // (titan-ops architecture/resource-lifecycle.md#kinds). The volumes' commands first: the lease hands out the
        // oldest row, and the VM's import attaches copies that must be there. Enqueued before the save: every row, its
        // audit entry and its command commit together (titan-ops architecture/provider-and-worker.md#provider-command-table).
        string projectName = TenantAggregate.ProjectNameFor(source.TenantId);
        var restoredWithIt = new List<VolumeRestoredWithItsVm>();
        foreach ((VolumeAggregate volume, VolumeAggregate copy, BackupRecord archive) in copies)
        {
            Guid createId = copy.Operation.PendingId!.Value;
            await commands.EnqueueVolumeRestoreAsync(volume, copy, createId, archive.BackupKey, projectName, ct);
            restoredWithIt.Add(new VolumeRestoredWithItsVm(volume.Id, copy.Id, createId));
        }
        await commands.EnqueueVmRestoreAsync(source, restoredVm, restoredVm.Operation.PendingId!.Value, vmArchive.BackupKey,
            projectName, restoredWithIt, ct);
        await vms.SaveAsync(ct);

        return new RestoreVmFromBackupCommandResult(restoredVm.Id);
    }
}

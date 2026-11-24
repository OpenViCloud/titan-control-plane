namespace Titan.Application.Commands.Admin.Backup.Contracts;

// A platform admin restores one backup archive of a VM as a new VM of the same tenant, with a copy of each volume
// attached to it (titan-ops features/backup/design.md#restore-as-new-resource). The VM id comes from the route; the
// owner is read from the VM, never from the caller.
public record RestoreVmFromBackupCommand(Guid VmId, string BackupKey, string Name);

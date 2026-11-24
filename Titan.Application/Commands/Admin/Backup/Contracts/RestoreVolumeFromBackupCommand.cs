namespace Titan.Application.Commands.Admin.Backup.Contracts;

// A platform admin restores one backup archive of a volume as a new volume of the same tenant
// (titan-ops features/backup/design.md#restore-as-new-resource). The volume id comes from the route; the owner is read
// from the volume, never from the caller.
public record RestoreVolumeFromBackupCommand(Guid VolumeId, string BackupKey, string Name);

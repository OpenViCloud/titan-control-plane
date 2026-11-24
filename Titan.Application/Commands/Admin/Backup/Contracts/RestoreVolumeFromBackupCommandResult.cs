namespace Titan.Application.Commands.Admin.Backup.Contracts;

// The restore makes a new volume, so its id is what comes back (titan-ops architecture/http-contract.md#create-returns-id).
public record RestoreVolumeFromBackupCommandResult(Guid NewVolumeId);

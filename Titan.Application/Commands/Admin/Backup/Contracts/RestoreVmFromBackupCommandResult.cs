namespace Titan.Application.Commands.Admin.Backup.Contracts;

// The restore makes a new VM, so its id is what comes back (titan-ops architecture/http-contract.md#create-returns-id);
// the copies of its volumes are read from the volume list.
public record RestoreVmFromBackupCommandResult(Guid NewVmId);

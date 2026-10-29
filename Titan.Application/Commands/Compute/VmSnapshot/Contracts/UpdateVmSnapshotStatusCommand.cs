namespace Titan.Application.Commands.Compute.VmSnapshot.Contracts;

// The worker's callback payload: an observation, but it writes, so it is a command. The route carries both ids.
//
// Report says which of five reports this is (titan-ops features/vm/design.md#vm-snapshot-reports): Observed (the reconciler:
// Exists), Created, Reverted and Deleted (the create, revert and delete commands; Created brings the id), and a
// command's give-up (Reason/Detail; the create's carries the id when Incus has the snapshot).
public record UpdateVmSnapshotStatusCommand(
    Guid VmId,
    Guid SnapshotId,
    string Report,
    bool? Exists = null,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);

namespace Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;

// Only the id (titan-ops architecture/http-contract.md#create-returns-id).
public record CreateVolumeSnapshotCommandResult(Guid SnapshotId);

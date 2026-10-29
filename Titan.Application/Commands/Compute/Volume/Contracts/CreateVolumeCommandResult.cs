namespace Titan.Application.Commands.Compute.Volume.Contracts;

// Only the id (titan-ops architecture/http-contract.md#create-returns-id).
public record CreateVolumeCommandResult(Guid VolumeId);

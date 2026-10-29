namespace Titan.Application.Commands.Compute.Volume.Contracts;

// {volumeId} comes from the route, VmId from the body — the endpoint composes both (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record AttachVolumeCommand(Guid VolumeId, Guid VmId);

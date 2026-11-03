namespace Titan.Application.Commands.Compute.Volume.Contracts;

// The reconciler's observation of one volume (titan-ops architecture/resource-lifecycle.md#callback-routes): whether Incus
// has it and, when it does, the instances in its used_by. VolumeId comes from the route.
public record ObserveVolumeCommand(Guid VolumeId, bool Exists, List<string>? AttachedTo = null);

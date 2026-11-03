namespace Titan.Domain.Aggregates.Volume;

/// What the worker read of the volume on Incus, resolved by the control plane: the Titan VM using it and whether an
/// instance outside Titan does (titan-ops features/volume/design.md#attachment-from-used-by). Carried by a command's
/// report; a volume the worker did not find has no observation.
public sealed record VolumeObservation(Guid? AttachedVmId, bool AttachedElsewhere);

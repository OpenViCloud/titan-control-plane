namespace Titan.Domain.Aggregates.Volume;

/// What Volume.Attach hands the handler for the command: the operation's id and the two provider ids, which leave only
/// past the volume's, the VM's and the cluster's checks (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public sealed record VolumeAttachProviderIds(Guid OperationId, string ExternalVolumeId, string ExternalVmId);

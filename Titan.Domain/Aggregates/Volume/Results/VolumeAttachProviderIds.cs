namespace Titan.Domain.Aggregates.Volume;

/// What Volume.Attach hands the handler for the command: the request's id and the two provider ids, which leave only
/// past the volume's, the VM's and the cluster's checks (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public sealed record VolumeAttachProviderIds(Guid RequestId, string ExternalVolumeId, string ExternalVmId);

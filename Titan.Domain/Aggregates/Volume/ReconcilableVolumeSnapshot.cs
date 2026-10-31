using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// One row of the storage reconciler's snapshot list: the columns themselves, not an aggregate
/// (titan-ops features/network/user-stories.md#us-net-intent-04).
public record ReconcilableVolumeSnapshot(Guid Id, Guid VolumeId, string ExternalId, CurrentExistence CurrentExistence);

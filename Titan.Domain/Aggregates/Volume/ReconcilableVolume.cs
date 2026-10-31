using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// One row of the storage reconciler's volume list: the columns themselves, not an aggregate
/// (titan-ops features/network/user-stories.md#us-net-intent-04).
public record ReconcilableVolume(
    Guid Id, string ExternalId, CurrentExistence CurrentExistence, Guid? CurrentAttachedVmId, bool CurrentAttachedElsewhere);

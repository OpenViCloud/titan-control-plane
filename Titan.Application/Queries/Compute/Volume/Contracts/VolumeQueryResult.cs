using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Compute.Volume;
using VolumeAggregate = Titan.Domain.Aggregates.Compute.Volume.Volume;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Compute.Volume.Contracts;

// The read side's own shape (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#no-contract-reuse): never reused by a command, even
// where fields would match. Incus has no status word for a volume (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#volume-state-is-existence):
// the current pair (existence, attachment) is what Incus was last seen to hold, the desired pair what was asked, and
// transition the request still waiting, derived. The failure detail is the operator's, never the tenant's.
public record VolumeQueryResult(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CurrentExistence CurrentExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DesiredExistence DesiredExistence,
    Guid? CurrentAttachedVmId,
    bool CurrentAttachedElsewhere,
    Guid? DesiredAttachedVmId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VolumeTransition? Transition,
    string ClusterId,
    string VolumeOfferingId,
    int SizeGb,
    DateTimeOffset CreatedAt,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt)
{
    public static VolumeQueryResult From(VolumeAggregate v) =>
        new(v.Id, v.Name, v.CurrentExistence, v.DesiredExistence, v.CurrentAttachedVmId,
            v.CurrentAttachedElsewhere, v.DesiredAttachedVmId, v.Transition, v.ClusterId,
            v.VolumeOfferingId, v.SizeGb, v.CreatedAt, v.RequestFailureReason, v.RequestFailedAt);
}

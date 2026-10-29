using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using VolumeSnapshotAggregate = Titan.Domain.Aggregates.Compute.VolumeSnapshot.VolumeSnapshot;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Compute.VolumeSnapshot.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse). Existence and transition, like
// VolumeQueryResult; the failure detail is the operator's, never the tenant's.
public record VolumeSnapshotQueryResult(
    Guid Id,
    Guid VolumeId,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CurrentExistence CurrentExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DesiredExistence DesiredExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VolumeSnapshotTransition? Transition,
    string? ExternalId,
    DateTimeOffset CreatedAt,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt)
{
    public static VolumeSnapshotQueryResult From(VolumeSnapshotAggregate s) =>
        new(s.Id, s.VolumeId, s.Name, s.CurrentExistence, s.DesiredExistence,
            s.Transition, s.ExternalId, s.CreatedAt, s.RequestFailureReason, s.RequestFailedAt);
}

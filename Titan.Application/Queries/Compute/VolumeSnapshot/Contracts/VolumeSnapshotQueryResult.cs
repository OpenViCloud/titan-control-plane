using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;

namespace Titan.Application.Queries.Compute.VolumeSnapshot.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse). One status
// (titan-ops features/volume/design.md#volume-snapshot); the failure detail is the operator's, never the tenant's.
public record VolumeSnapshotQueryResult(
    Guid Id,
    Guid VolumeId,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VolumeSnapshotStatus Status,
    string? ExternalId,
    DateTimeOffset CreatedAt,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt)
{
    public static VolumeSnapshotQueryResult From(VolumeSnapshotEntity s) =>
        new(s.Id, s.VolumeId, s.Name, s.Status, s.ExternalId, s.CreatedAt, s.Operation.FailureReason, s.Operation.FailedAt);
}

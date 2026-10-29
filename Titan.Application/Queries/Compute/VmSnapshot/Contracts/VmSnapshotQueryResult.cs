using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using VmSnapshotAggregate = Titan.Domain.Aggregates.Compute.VmSnapshot.VmSnapshot;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Compute.VmSnapshot.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse); the create command returns
// only an id (titan-ops architecture/http-contract.md#create-returns-id). Existence and transition
// (titan-ops features/vm/design.md#vm-snapshot-is-existence); the failure detail is the operator's, never the tenant's.
public record VmSnapshotQueryResult(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CurrentExistence CurrentExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DesiredExistence DesiredExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VmSnapshotTransition? Transition,
    DateTime CreatedAt,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt)
{
    public static VmSnapshotQueryResult From(VmSnapshotAggregate s) =>
        new(s.Id, s.Name, s.CurrentExistence, s.DesiredExistence, s.Transition,
            s.CreatedAt, s.RequestFailureReason, s.RequestFailedAt);
}

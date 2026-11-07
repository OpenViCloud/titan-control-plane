using System.Text.Json.Serialization;
using VmSnapshotStatus = Titan.Domain.Aggregates.Vm.VmSnapshotStatus;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;

namespace Titan.Application.Queries.Compute.VmSnapshot.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse); the create command returns
// only an id (titan-ops architecture/http-contract.md#create-returns-id). One status
// (titan-ops features/vm/design.md#vm-snapshot); the failure detail is the operator's, never the tenant's.
public record VmSnapshotQueryResult(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VmSnapshotStatus Status,
    DateTime CreatedAt,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt)
{
    public static VmSnapshotQueryResult From(VmSnapshotEntity s) =>
        new(s.Id, s.Name, s.Status, s.CreatedAt, s.Operation.FailureReason, s.Operation.FailedAt);
}

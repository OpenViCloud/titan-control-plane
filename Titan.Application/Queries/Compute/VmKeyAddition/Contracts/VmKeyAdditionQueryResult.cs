using System.Text.Json.Serialization;
using VmKeyAdditionStatus = Titan.Domain.Aggregates.Vm.VmKeyAdditionStatus;
using VmKeyAdditionEntity = Titan.Domain.Aggregates.Vm.VmKeyAddition;

namespace Titan.Application.Queries.Compute.VmKeyAddition.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse); the create command returns
// only an id (titan-ops architecture/http-contract.md#create-returns-id). One status
// (titan-ops features/vm/design.md#ssh-key); the failure detail is the operator's, never the tenant's.
public record VmKeyAdditionQueryResult(
    Guid Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VmKeyAdditionStatus Status,
    IReadOnlyList<Guid> SshKeyIds,
    bool RestartNeeded,
    DateTime CreatedAt,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt)
{
    public static VmKeyAdditionQueryResult From(VmKeyAdditionEntity a) =>
        new(a.Id, a.Status, a.SshKeyIds, a.RestartNeeded, a.CreatedAt, a.FailureReason, a.FailedAt);
}

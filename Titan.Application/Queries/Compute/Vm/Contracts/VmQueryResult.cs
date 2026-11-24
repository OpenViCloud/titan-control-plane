using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Vm;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Application.Queries.Compute.Vm.Contracts;

// The read side's own view of a VM, not shared with the command side (titan-ops architecture/code-structure.md#contracts):
// UpdateVm answers 204 and the caller re-reads.
//
// Status is the VM's one status (titan-ops features/vm/design.md#vm-status). RequestFailureReason is the fixed text of
// the last request that failed; the raw error is admin-only. RestoredFromBackup marks a VM an admin restored from a
// backup: it does not count toward the tenant's quota (titan-ops features/backup/design.md#restore-as-new-resource),
// so a client that counts usage from the list leaves it out.
public record VmQueryResult(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VmStatus Status,
    string? IpAddress,
    Guid NetworkId,
    string VmOfferingId,
    string TemplateId,
    string ClusterId,
    int DiskSizeGb,
    string? UserData,
    DateTimeOffset CreatedAt,
    string? PublicIpv6Address = null,
    string? RequestFailureReason = null,
    DateTimeOffset? RequestFailedAt = null,
    int StopTimeoutSeconds = VmAggregate.DefaultStopTimeoutSeconds,
    bool RestoredFromBackup = false)
{
    public static VmQueryResult From(VmAggregate v) =>
        new(v.Id, v.Name, v.Status, v.IpAddress,
            v.NetworkId, v.VmOfferingId, v.TemplateId, v.ClusterId, v.DiskSizeGb, v.UserData, v.CreatedAt,
            v.PublicIpv6Address, v.Operation.FailureReason, v.Operation.FailedAt, v.StopTimeoutSeconds,
            v.RestoredFromBackup);
}

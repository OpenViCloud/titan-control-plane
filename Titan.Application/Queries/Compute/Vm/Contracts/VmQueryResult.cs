using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Vm;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using Titan.Domain.Values;

namespace Titan.Application.Queries.Compute.Vm.Contracts;

// The read side's own view of a VM, not shared with the command side (titan-ops architecture/code-structure.md#contracts):
// RenameVm answers 204 and the caller re-reads.
//
// CurrentState is the Incus instance status word for word, null while Incus has no instance; CurrentExistence
// says whether it does. The desired pair is what the customer asked for, and Transition the request still
// waiting (null = none; null with the pairs differing is drift)
// (titan-ops architecture/desired-and-current.md#desired-and-current). RequestFailureReason is the fixed text of a
// create the provider refused; the raw error is admin-only.
public record VmQueryResult(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VmState? CurrentState,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VmDesiredState DesiredState,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] CurrentExistence CurrentExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] DesiredExistence DesiredExistence,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VmTransition? Transition,
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
    DateTimeOffset? RequestFailedAt = null)
{
    public static VmQueryResult From(VmAggregate v) =>
        new(v.Id, v.Name, v.CurrentState, v.DesiredState, v.CurrentExistence,
            v.DesiredExistence, v.Transition, v.IpAddress,
            v.NetworkId, v.VmOfferingId, v.TemplateId, v.ClusterId, v.DiskSizeGb, v.UserData, v.CreatedAt,
            v.PublicIpv6Address, v.RequestFailureReason, v.RequestFailedAt);
}

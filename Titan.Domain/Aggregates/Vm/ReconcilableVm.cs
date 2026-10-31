using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// One row of the VM reconciler's list: the columns themselves, not an aggregate
/// (titan-ops features/network/user-stories.md#us-net-intent-04).
public record ReconcilableVm(
    Guid Id, string ExternalId, VmState? CurrentState, CurrentExistence CurrentExistence, string? IpAddress, string? PublicIpv6Address);

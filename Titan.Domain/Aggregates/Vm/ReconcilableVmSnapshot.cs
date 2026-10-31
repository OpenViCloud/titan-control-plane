using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// One row of the VM snapshot reconciler's list: the columns themselves, not an aggregate
/// (titan-ops features/network/user-stories.md#us-net-intent-04).
public record ReconcilableVmSnapshot(Guid Id, Guid VmId, string ExternalId, CurrentExistence CurrentExistence);

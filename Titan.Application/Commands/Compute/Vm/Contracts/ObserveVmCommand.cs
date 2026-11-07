namespace Titan.Application.Commands.Compute.Vm.Contracts;

// The reconciler's observation of one VM (titan-ops architecture/resource-lifecycle.md#callback-routes): whether Incus has the
// instance, and what it read when it does. An observation records the Incus status and ends no request
// (titan-ops architecture/resource-lifecycle.md#invariants).
public record ObserveVmCommand(Guid VmId, bool Exists, VmObservationReport? Observed = null);

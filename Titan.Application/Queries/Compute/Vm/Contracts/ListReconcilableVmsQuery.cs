namespace Titan.Application.Queries.Compute.Vm.Contracts;

// The VM reconciler asks for the VMs of one cluster it must observe.
public record ListReconcilableVmsQuery(string ClusterId);

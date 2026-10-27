namespace Titan.Application.Queries.Compute.VmSnapshot.Contracts;

// The VM snapshot reconciler asks for the snapshots of one cluster's VMs it must observe.
public record ListReconcilableVmSnapshotsQuery(string ClusterId);

namespace Titan.Domain.Aggregates.Vm;

/// The new snapshot and the provider id of its VM, handed out together past the VM's own check
/// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public sealed record VmSnapshotTicket(VmSnapshot Snapshot, string ExternalVmId);

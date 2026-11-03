namespace Titan.Application.Commands.Compute.VmSnapshot.Contracts;

// The reconciler's observation of one VM snapshot: whether its instance on Incus has it. Both ids come from the route.
public record ObserveVmSnapshotCommand(Guid VmId, Guid SnapshotId, bool Exists);

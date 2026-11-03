namespace Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;

// The reconciler's observation of one volume snapshot: whether Incus has it (a snapshot whose volume is gone has not).
public record ObserveVolumeSnapshotCommand(Guid VolumeId, Guid SnapshotId, bool Exists);

using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class VmSnapshot
{
    /// The revert is the waiting operation, with the id the handler chose; the snapshot must be Available, Incus holding
    /// it with nothing waiting. The VM's own rule is checked by Vm.RevertSnapshot before this is reached.
    internal void Revert(Guid operationId)
    {
        if (Status != VmSnapshotStatus.Available)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to revert.");
        Status = VmSnapshotStatus.Reverting;
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, VmSnapshotStatus.Available);
    }

    /// The revert command ended Done: the disk is back at the snapshot, which stays (titan-ops
    /// features/vm/design.md#vm-snapshot-reports). The snapshot rests as Incus was last seen holding it. Ignored, returning
    /// false, when it names another operation than the revert the snapshot waits on.
    internal bool RevertSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, VmSnapshotStatus.Reverting))
        {
            return false;
        }
        VmSnapshotStatus resting = RestingStatus;
        Operation = Operation.Done();
        Status = resting;
        return true;
    }

    /// The revert command gave up: records the reason; the snapshot rests as Incus was last seen holding it. Ignored,
    /// returning false, when it names another operation than the revert the snapshot waits on.
    internal bool RevertFailed(Guid operationId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VmSnapshotStatus.Reverting))
        {
            return false;
        }
        VmSnapshotStatus resting = RestingStatus;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }
}

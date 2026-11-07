using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class VmSnapshot
{
    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; it is Deleting
    /// and waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm),
    /// remembering whether Incus held it. Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    /// A snapshot that never reached Incus is deleted here, at once: IsDeleted tells the handler there is no command
    /// to send (titan-ops architecture/desired-and-current.md#delete-locally).
    internal void Delete(Guid operationId)
    {
        if (Status == VmSnapshotStatus.Deleting)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsDeleted)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && Status == VmSnapshotStatus.Failed;
        if (neverReachedIncus)
        {
            Status = VmSnapshotStatus.Deleted;
            Operation = Operation.ReturnedToRest();
            return;
        }
        if (IsTransitionStatus || ExternalId is null)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must be on Incus, with nothing waiting, to delete.");
        VmSnapshotStatus? seen = InstanceStatus;
        Status = VmSnapshotStatus.Deleting;
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, seen);
    }

    /// The delete command ended Done: the snapshot is gone. Ignored, returning false, when it names another operation
    /// than the delete the snapshot waits on.
    internal bool DeleteSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, VmSnapshotStatus.Deleting))
        {
            return false;
        }
        Status = VmSnapshotStatus.Deleted;
        Operation = Operation.Done();
        return true;
    }

    /// The delete command gave up: the snapshot rests again as Incus was last seen holding it, with the reason; still on
    /// Incus, it counts against the quota again, and the next delete is a new operation. Ignored, returning false, when it
    /// names another operation than the delete the snapshot waits on.
    internal bool DeleteFailed(Guid operationId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VmSnapshotStatus.Deleting))
        {
            return false;
        }
        VmSnapshotStatus resting = RestingStatus;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }

    /// Incus deleted the instance, and this snapshot with it: the VM's delete Done ends the snapshot, whatever it waited
    /// on. A snapshot already Deleted stays as it is.
    internal void EndWithItsVm()
    {
        if (IsDeleted)
        {
            return;
        }
        Status = VmSnapshotStatus.Deleted;
        Operation = Operation.ReturnedToRest();
    }
}

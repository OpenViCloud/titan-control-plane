using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

public partial class VolumeSnapshot
{
    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; it is Deleting
    /// and waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm),
    /// remembering whether Incus held it. Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    /// A snapshot that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    internal OperationTicket? Delete()
    {
        if (Status == VolumeSnapshotStatus.Deleting)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsDeleted)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && Status == VolumeSnapshotStatus.Failed;
        if (neverReachedIncus)
        {
            Status = VolumeSnapshotStatus.Deleted;
            Operation = Operation.ReturnedToRest();
            return null;
        }
        if (IsTransitionStatus || ExternalId is null)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot is not yet on Incus.");
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, InstanceStatus);
        Status = VolumeSnapshotStatus.Deleting;
        return OperationTicket.Of(operationId, ExternalId);
    }

    /// The delete command ended Done: the snapshot is gone. Ignored, returning false, when it names another operation
    /// than the delete the snapshot waits on.
    internal bool DeleteSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, VolumeSnapshotStatus.Deleting))
        {
            return false;
        }
        Operation = Operation.Done();
        Status = VolumeSnapshotStatus.Deleted;
        return true;
    }

    /// The delete command gave up: the snapshot rests again as Incus was last seen holding it, with the reason; still on
    /// Incus, it counts against the quota again, and the next delete is a new operation. Ignored, returning false, when
    /// it names another operation than the delete the snapshot waits on.
    internal bool DeleteFailed(Guid operationId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VolumeSnapshotStatus.Deleting))
        {
            return false;
        }
        VolumeSnapshotStatus resting = RestingStatus;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }

    /// Incus deleted the volume, and this snapshot with it: the volume's delete Done ends the snapshot, whatever it waited
    /// on. A snapshot already Deleted stays as it is.
    internal void EndWithItsVolume()
    {
        if (IsDeleted)
        {
            return;
        }
        Operation = Operation.ReturnedToRest();
        Status = VolumeSnapshotStatus.Deleted;
    }
}

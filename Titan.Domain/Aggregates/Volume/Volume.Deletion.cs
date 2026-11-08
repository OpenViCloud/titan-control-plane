using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

public partial class Volume
{
    /// Follows Incus: a volume with a provider id is deleted through Incus, even one Incus no longer has; it is Deleting
    /// and waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm),
    /// remembering whether Incus held it. Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat),
    /// and refuses while a snapshot it holds still counts: Incus deletes a volume's snapshots with it.
    /// A volume that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    public OperationTicket? Delete()
    {
        if (Status == VolumeStatus.Deleting)
            throw new BusinessException("VOLUME_INVALID_STATE", "A delete is already in progress for this volume.");
        if (IsDeleted)
            throw new BusinessException("VOLUME_INVALID_STATE", "The volume is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && Status == VolumeStatus.Failed;
        if (neverReachedIncus)
        {
            Status = VolumeStatus.Deleted;
            Operation = Operation.ReturnedToRest();
            return null;
        }
        if (IsTransitionStatus || ExternalId is null)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to delete.");
        if (IsInUse)
            throw new BusinessException("VOLUME_INVALID_STATE", "Detach the volume before deleting it.");
        // Deleting a volume deletes its snapshots too (titan-ops features/volume/design.md#delete-refused-with-snapshots).
        bool hasLiveSnapshots = SnapshotQuotaUsage > 0;
        if (hasLiveSnapshots)
            throw new BusinessException("VOLUME_HAS_SNAPSHOTS", "Delete the volume's snapshots before the volume.");
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, InstanceStatus);
        Status = VolumeStatus.Deleting;
        return OperationTicket.Of(operationId, ExternalId);
    }

    /// The delete command ended Done: the volume is gone, with the attachment cleared, and Incus deleted its snapshots
    /// with it (titan-ops features/volume/design.md#delete-refused-with-snapshots): every snapshot not yet Deleted goes
    /// with the volume. Ignored, returning false, when it names another operation than the delete the volume waits on.
    public bool DeleteSucceeded(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Deleting))
        {
            return false;
        }
        NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Done();
        Status = VolumeStatus.Deleted;
        CurrentAttachedVmId = null;
        CurrentAttachedElsewhere = false;
        foreach (VolumeSnapshot snapshot in _snapshots)
        {
            snapshot.EndWithItsVolume();
        }
        return true;
    }

    /// The delete command gave up: the volume rests again as Incus was seen holding it, with the reason; still on Incus,
    /// it counts against the quota again, and the next delete is a new operation. Ignored, returning false, when it
    /// names another operation than the delete the volume waits on.
    public bool DeleteFailed(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Deleting))
        {
            return false;
        }
        VolumeStatus? seen = NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = seen ?? VolumeStatus.NotFound;
        return true;
    }
}

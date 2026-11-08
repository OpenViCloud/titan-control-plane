using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Volume;

public partial class Volume
{
    /// A volume restored from a snapshot: the snapshot's readiness is an invariant of the creation (titan-ops features/volume/design.md#volume-restore-is-a-new-volume);
    /// sizing and cluster come from the source volume, which the caller resolves.
    public static Volume Create(
        Guid tenantId,
        string name, string clusterId, string volumeOfferingId, int sizeGb,
        VolumeSnapshot? restoredFrom = null)
    {
        if (restoredFrom != null)
        {
            restoredFrom.EnsureRestorable();
        }
        var volume = new Volume
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            ClusterId = clusterId,
            VolumeOfferingId = volumeOfferingId,
            SizeGb = sizeGb,
            Status = VolumeStatus.Creating,
            CreatedAt = DateTimeOffset.UtcNow
        };
        volume.Operation = volume.Operation.Asked(Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        return volume;
    }

    /// The create command ended Done: records the id, and the volume is Available. A Done must leave the volume named on
    /// Incus, or nothing would be left to delete it through: without an id it is refused before anything is written (a
    /// worker that sends it has a bug, and does not retry a 422). A report that names another operation than the create
    /// the volume waits on is ignored and returns false. sawTheVolume false: the worker did not read the volume on Incus,
    /// so the attachment is not read either.
    public bool CreateSucceeded(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Creating))
        {
            return false;
        }
        bool doneWithoutAnId = ExternalId == null && string.IsNullOrEmpty(externalId);
        if (doneWithoutAnId)
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }
        NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Done();
        Status = VolumeStatus.Available;
        return true;
    }

    /// The create command gave up: records the reason and what the worker saw. Before Incus held a volume (the report
    /// brings no id) it is Failed, and may be deleted, not retried; after (the report names it) it is Available. Ignored,
    /// returning false, when it names another operation than the create the volume waits on.
    public bool CreateFailed(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Creating))
        {
            return false;
        }
        NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        if (ExternalId is null)
        {
            Status = VolumeStatus.Failed;
        }
        else
        {
            Status = VolumeStatus.Available;
        }
        return true;
    }
}

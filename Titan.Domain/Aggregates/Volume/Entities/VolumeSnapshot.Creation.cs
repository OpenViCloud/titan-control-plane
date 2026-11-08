using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

public partial class VolumeSnapshot
{
    /// Called by Volume.AddSnapshot after the volume checked itself, with the id the handler chose; the create's id is
    /// made here.
    internal static VolumeSnapshot Create(Guid id, Guid volumeId, Guid tenantId, string name)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "A snapshot needs an id.");
        }
        var snapshot = new VolumeSnapshot
        {
            Id = id,
            VolumeId = volumeId,
            TenantId = tenantId,
            Name = name,
            Status = VolumeSnapshotStatus.Creating,
            CreatedAt = DateTimeOffset.UtcNow
        };
        snapshot.Operation = snapshot.Operation.Asked(Guid.NewGuid(), DateTimeOffset.UtcNow, null);
        return snapshot;
    }

    /// The create this snapshot waits on, for its command. The snapshot is not on Incus yet, so the command acts on its
    /// volume there: the ticket carries the volume's provider id, which the volume hands in after its own check.
    internal OperationTicket TicketForCreate(string volumeProviderId)
    {
        if (Status != VolumeSnapshotStatus.Creating || Operation.PendingId == null)
        {
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "No create is waiting for this snapshot.");
        }
        return OperationTicket.Of(Operation.PendingId.Value, volumeProviderId);
    }

    /// The create command ended Done: records the id, and the snapshot is Available. A Done must leave the snapshot named
    /// on Incus, or nothing would be left to delete it through: without an id it is refused before anything is written (a
    /// worker that sends it has a bug, and does not retry a 422). A report that names another operation than the create
    /// the snapshot waits on is ignored and returns false.
    internal bool CreateSucceeded(Guid operationId, string? externalId)
    {
        if (!IsWaitingOn(operationId, VolumeSnapshotStatus.Creating))
        {
            return false;
        }
        bool doneWithoutAnId = ExternalId == null && string.IsNullOrEmpty(externalId);
        if (doneWithoutAnId)
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }
        RecordTheId(externalId);
        Operation = Operation.Done();
        Status = VolumeSnapshotStatus.Available;
        return true;
    }

    /// The create command gave up: records the reason. Before Incus held a snapshot (the report brings no id) it is Failed;
    /// after (the report names it) it is Available. Ignored, returning false, when it names another operation than the
    /// create the snapshot waits on.
    internal bool CreateFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VolumeSnapshotStatus.Creating))
        {
            return false;
        }
        RecordTheId(externalId);
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        if (ExternalId is null)
        {
            Status = VolumeSnapshotStatus.Failed;
        }
        else
        {
            Status = VolumeSnapshotStatus.Available;
        }
        return true;
    }
}

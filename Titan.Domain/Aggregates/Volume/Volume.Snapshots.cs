using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

public partial class Volume
{
    // ---- Snapshots: the volume counts its snapshots and routes each call to the snapshot it names
    // (titan-ops architecture/code-structure.md#child-entity). Logic stays on VolumeSnapshot; this is the way in. A
    // snapshot's operations do not touch the volume's status.

    /// Intent: a new snapshot of this volume, with the id the handler chose. The volume must rest on Incus
    /// (EnsureSnapshottable). The create operation is the snapshot's own; its ticket carries the volume's
    /// provider id, the volume the command snapshots.
    public OperationTicket AddSnapshot(Guid snapshotId, string name)
    {
        EnsureSnapshottable();
        VolumeSnapshot snapshot = VolumeSnapshot.Create(snapshotId, Id, TenantId, name);
        _snapshots.Add(snapshot);
        return snapshot.TicketForCreate(GetExternalId());
    }

    /// A snapshot this volume does not have answers like a missing one, the same 404 a snapshot of another volume gets
    /// (titan-ops features/volume/design.md#volume-snapshot).
    private VolumeSnapshot GetSnapshot(Guid snapshotId)
    {
        foreach (VolumeSnapshot snapshot in _snapshots)
        {
            if (snapshot.Id == snapshotId)
            {
                return snapshot;
            }
        }
        throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
    }

    /// Intent on one snapshot: its delete goes through Incus; returns the operation and the snapshot's provider id for
    /// the command, or no ticket when the snapshot never reached Incus and is deleted here.
    public OperationTicket? DeleteSnapshot(Guid snapshotId) => GetSnapshot(snapshotId).Delete();

    /// A restore starts from a snapshot resting on Incus and hands out its provider id
    /// (titan-ops features/volume/design.md#volume-restore-is-a-new-volume); the new volume is Create's.
    public string ProviderIdForSnapshotRestore(Guid snapshotId) => GetSnapshot(snapshotId).ProviderIdForRestore();

    /// The reports of the snapshot's own commands (titan-ops features/volume/design.md#volume-reports); each is false when
    /// it names another operation than the one the snapshot waits on, which its status says.
    public bool SnapshotCreateSucceeded(Guid snapshotId, Guid operationId, string? externalId) =>
        GetSnapshot(snapshotId).CreateSucceeded(operationId, externalId);

    public bool SnapshotCreateFailed(Guid snapshotId, Guid operationId, string? externalId, string? reason, string? detail) =>
        GetSnapshot(snapshotId).CreateFailed(operationId, externalId, reason, detail);

    public bool SnapshotDeleteSucceeded(Guid snapshotId, Guid operationId) =>
        GetSnapshot(snapshotId).DeleteSucceeded(operationId);

    public bool SnapshotDeleteFailed(Guid snapshotId, Guid operationId, string? reason, string? detail) =>
        GetSnapshot(snapshotId).DeleteFailed(operationId, reason, detail);

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): they record
    // what Incus has and finish no operation.

    public void MarkSnapshotObserved(Guid snapshotId) => GetSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => GetSnapshot(snapshotId).MarkNotFound();

    /// The precondition of AddSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to create a snapshot.");
    }
}

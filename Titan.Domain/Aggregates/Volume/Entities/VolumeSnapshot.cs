using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Volume;

/// One snapshot of a volume: a copy on Incus named under the volume
/// (titan-ops features/volume/design.md#volume-snapshot). A child of Volume (titan-ops architecture/code-structure.md#child-entity):
/// "a volume with a live snapshot is not deleted" and "the volume's delete takes its snapshots"
/// are the volume's invariants, so only the root reaches the methods below, and handlers go through
/// Volume.AddSnapshot, DeleteSnapshot, ProviderIdForSnapshotRestore, SnapshotCreateSucceeded, SnapshotCreateFailed,
/// SnapshotDeleteSucceeded, SnapshotDeleteFailed and MarkSnapshot*.
public partial class VolumeSnapshot : IEntity
{
    /// A snapshot's name: 1 to 100 characters, not blank, any characters. The validator reads it. A property, not a const: a child
    /// has no public field.
    public static int NameMaxLength { get; } = 100;

    public Guid Id { get; private set; }
    public Guid VolumeId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    /// The one status the API shows (titan-ops features/volume/design.md#volume-snapshot). Only an operation, a report ending
    /// it, the volume's delete taking it along, or an observation at rest writes it, each through the moves of
    /// VolumeSnapshot.Creation.cs, VolumeSnapshot.Deletion.cs and VolumeSnapshot.Observation.cs.
    public VolumeSnapshotStatus Status { get; private set; }
    /// The snapshot's own operation, independent of its volume's, the last one that failed, and whether Incus was last
    /// seen holding the snapshot while one waits.
    public VolumeSnapshotOperation Operation { get; private set; } = VolumeSnapshotOperation.None;
    public string? ExternalId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private VolumeSnapshot() { }

    /// Whether Incus holds the snapshot as far as Titan last knew: Available, or null. While an operation waits it is what
    /// the operation last saw; at rest it is Available when the status is. Worked out, never stored, and not on /v1.
    public VolumeSnapshotStatus? InstanceStatus
    {
        get
        {
            if (IsTransitionStatus)
            {
                return Operation.LastSeenStatus;
            }
            if (Status == VolumeSnapshotStatus.Available)
            {
                return VolumeSnapshotStatus.Available;
            }
            return null;
        }
    }

    /// An operation waits on its command: only the report carrying its id moves the status on.
    private bool IsTransitionStatus =>
        Status == VolumeSnapshotStatus.Creating || Status == VolumeSnapshotStatus.Deleting;

    /// Billed while Incus holds it, a delete in flight included (titan-ops features/volume/design.md#counting-and-billing).
    /// VolumeSnapshotRepository.ListBillableAsync writes the same test in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsBilled => InstanceStatus == VolumeSnapshotStatus.Available;

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// Until then the snapshot is still listed under its volume and still observed; the repository's list and
    /// reconciler queries write status <> 'Deleted' in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => Status == VolumeSnapshotStatus.Deleted;

    /// A delete was asked and still waits, or the snapshot is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => Status == VolumeSnapshotStatus.Deleting || IsDeleted;

    /// Counted by occupancy toward MaxVolumeSnapshotsPerVolume, and the same count says whether the volume may be
    /// deleted (titan-ops features/volume/design.md#counting-and-billing): what Incus holds or is about to hold. A delete
    /// asked for stops counting at once; a failed create with nothing on Incus, and a snapshot Incus lost, do not count.
    public bool CountsTowardQuota =>
        Status == VolumeSnapshotStatus.Creating || Status == VolumeSnapshotStatus.Available;

    /// Incus holds it and nothing waits: the only situation a snapshot can be restored from.
    private bool RestsOnIncus => Status == VolumeSnapshotStatus.Available;

    /// The report names the operation the snapshot waits on, which its status says.
    private bool IsWaitingOn(Guid operationId, VolumeSnapshotStatus waiting) =>
        Status == waiting && Operation.PendingId == operationId;

    /// The status a delete that gave up goes back to: whether Incus was last seen holding the snapshot.
    private VolumeSnapshotStatus RestingStatus => InstanceStatus ?? VolumeSnapshotStatus.NotFound;

    /// A report brings the provider id with it, once.
    private void RecordTheId(string? externalId)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
        }
    }

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// Restore creates a new volume and leaves this snapshot alone, so the check is called from
    /// Volume.Create and Volume.ProviderIdForSnapshotRestore, never from a handler.
    internal void EnsureRestorable()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to restore.");
    }

    /// Volume.Create checks the same rule; this hands out the id, and the caller needs both,
    /// so neither can be skipped. Reached through Volume.ProviderIdForSnapshotRestore.
    internal string ProviderIdForRestore()
    {
        EnsureRestorable();
        return ExternalId ?? throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public for the storage
    /// reconciler's read model.
    public string GetExternalId()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");
        }
        return ExternalId;
    }
}

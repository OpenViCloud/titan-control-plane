using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// One snapshot of a volume: a copy on Incus named under the volume
/// (titan-ops features/volume/design.md#volume-snapshot). A child of Volume (titan-ops architecture/code-structure.md#child-entity):
/// "a volume with a live snapshot is not deleted" and "the volume's delete takes its snapshots"
/// are the volume's invariants, so only the root reaches the methods below, and handlers go through
/// Volume.AddSnapshot, DeleteSnapshot, ProviderIdForSnapshotRestore, FinishSnapshotOperation, FailSnapshotOperation
/// and MarkSnapshot*.
public class VolumeSnapshot : ProviderResource<VolumeSnapshotOperationKind>, IEntity
{
    /// A snapshot's name: 1 to 100 characters, not blank, any characters. The validator reads it. A property, not a const: a child
    /// has no public field.
    public static int NameMaxLength { get; } = 100;

    public Guid Id { get; private set; }
    public Guid VolumeId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    // Incus has no status word for a volume snapshot, only whether it is there
    // (titan-ops features/volume/design.md#volume-state-is-existence). Mark* and an operation's report write CurrentExistence;
    // an operation, and a report that it failed, write DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current).
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public string? ExternalId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// Billed while Incus holds it, a delete in flight included (titan-ops features/volume/design.md#counting-and-billing).
    /// VolumeSnapshotRepository.ListBillableAsync writes the same test in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsBilled => CurrentExistence == CurrentExistence.Exists;

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// Until then the snapshot is still listed under its volume and still observed; the repository's list and
    /// reconciler queries write !IsDeleted in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => CurrentExistence == CurrentExistence.Deleted;

    private VolumeSnapshot() { }

    /// The move the waiting operation makes, for display.
    public VolumeSnapshotTransition? Transition
    {
        get
        {
            switch (PendingOperationKind)
            {
                case VolumeSnapshotOperationKind.Create:
                    return VolumeSnapshotTransition.Creating;
                case VolumeSnapshotOperationKind.Delete:
                    return VolumeSnapshotTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingOperationKind == VolumeSnapshotOperationKind.Create;
    private bool DeleteIsWaiting => PendingOperationKind == VolumeSnapshotOperationKind.Delete;
    /// A delete was asked and still waits, or the snapshot is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsDeleted;

    /// Counted by occupancy toward MaxVolumeSnapshotsPerVolume, and the same count says whether the volume may be
    /// deleted (titan-ops features/volume/design.md#counting-and-billing): what Incus holds or is about to hold. A delete
    /// asked for stops counting at once; a failed create with nothing on Incus, and a snapshot Incus lost, do not count.
    public bool CountsTowardQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && CreateIsWaiting));

    /// Called by Volume.AddSnapshot after the volume checked itself, with the id the handler chose.
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
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            CreatedAt = DateTimeOffset.UtcNow
        };
        snapshot.SetPending(NewOperation(VolumeSnapshotOperationKind.Create));
        return snapshot;
    }

    /// The create this snapshot waits on, for its command. The snapshot is not on Incus yet, so the command acts on its
    /// volume there: the ticket carries the volume's provider id, which the volume hands in after its own check.
    internal OperationTicket TicketForCreate(string volumeProviderId)
    {
        PendingOperation<VolumeSnapshotOperationKind>? waiting = Pending;
        if (waiting == null || waiting.Kind != VolumeSnapshotOperationKind.Create)
        {
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "No create is waiting for this snapshot.");
        }
        return OperationTicket.Of(waiting.Id, volumeProviderId);
    }

    /// Incus holds it and nothing waits: the only situation a snapshot can be restored from.
    private bool RestsOnIncus =>
        PendingOperationId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// Restore creates a new volume and leaves this snapshot alone, so the check is called from
    /// Volume.Create and Volume.ProviderIdForSnapshotRestore, never from a handler.
    internal void EnsureRestorable()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to restore.");
    }

    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    /// A snapshot that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    internal OperationTicket? Delete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsDeleted)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && PendingOperationId == null;
        if (neverReachedIncus)
        {
            DesiredExistence = DesiredExistence.Deleted;
            CurrentExistence = CurrentExistence.Deleted;
            SetPending(null);
            return null;
        }
        if (CreateIsWaiting || ExternalId is null)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot is not yet on Incus.");
        PendingOperation<VolumeSnapshotOperationKind> operation = NewOperation(VolumeSnapshotOperationKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(operation);
        return OperationTicket.Of(operation.Id, ExternalId);
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

    // ---- The report of the snapshot's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends its operation.

    /// The command ended Done. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null. The create records the id and existence, the delete ends the snapshot
    /// (titan-ops architecture/resource-lifecycle.md#kinds).
    internal VolumeSnapshotOperationKind? FinishOperation(Guid operationId, string? externalId)
    {
        if (IsDeleted) return null;
        PendingOperation<VolumeSnapshotOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        EnsureProviderIdOnDone(ExternalId, externalId);
        RecordWhatTheWorkerSaw(externalId);

        ClearFailure();
        if (waiting.Kind == VolumeSnapshotOperationKind.Delete)
        {
            CurrentExistence = CurrentExistence.Deleted;
        }

        SetPending(null);
        return waiting.Kind;
    }

    /// The command gave up. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null. Records the reason, keeps an id the worker found, and gives a delete back
    /// (titan-ops architecture/resource-lifecycle.md#kinds).
    internal VolumeSnapshotOperationKind? FailOperation(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (IsDeleted) return null;
        PendingOperation<VolumeSnapshotOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RecordWhatTheWorkerSaw(externalId);

        RecordFailure(reason, detail);
        if (waiting.Kind == VolumeSnapshotOperationKind.Delete)
        {
            DesiredExistence = DesiredExistence.Exists;
        }

        SetPending(null);
        return waiting.Kind;
    }

    private PendingOperation<VolumeSnapshotOperationKind>? WaitingOperationNamed(Guid operationId)
    {
        PendingOperation<VolumeSnapshotOperationKind>? waiting = Pending;
        bool reportIsForThePendingOperation = waiting != null && waiting.Id == operationId;
        if (!reportIsForThePendingOperation)
        {
            return null;
        }
        return waiting;
    }

    private void RecordWhatTheWorkerSaw(string? externalId)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
            CurrentExistence = CurrentExistence.Exists;
        }
    }

    /// Incus deleted the volume, and with it this snapshot: the volume's delete Done ends it, whatever it waited on.
    /// A snapshot already Deleted stays as it is.
    internal void EndWithItsVolume()
    {
        if (IsDeleted)
        {
            return;
        }
        CurrentExistence = CurrentExistence.Deleted;
        SetPending(null);
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, never throws,
    // ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler found the snapshot on Incus.
    internal void MarkObserved()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler did not find the snapshot, or its volume. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.NotFound;
    }
}

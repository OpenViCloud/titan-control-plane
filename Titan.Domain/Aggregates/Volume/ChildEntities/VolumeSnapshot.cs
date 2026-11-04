using System.Linq.Expressions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

/// One snapshot of a volume: a copy on Incus named under the volume
/// (titan-ops features/volume/design.md#volume-snapshot). A child of Volume (titan-ops architecture/code-structure.md#child-entity):
/// "a volume with a live snapshot is not deleted" and "the volume's delete takes its snapshots"
/// are the volume's invariants, so only the root reaches the methods below, and handlers go through
/// Volume.AddSnapshot, RequestSnapshotDelete, ProviderIdForSnapshotRestore, EndSnapshotRequest and MarkSnapshot*.
public class VolumeSnapshot : RequestingChild<VolumeSnapshotRequestKind>
{
    /// A snapshot's name: 1 to 100 characters, not blank, any characters. The validator reads it. A property, not a const: a child
    /// has no public field.
    public static int NameMaxLength { get; } = 100;

    public Guid Id { get; private set; }
    public Guid VolumeId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    // Incus has no status word for a volume snapshot, only whether it is there
    // (titan-ops features/volume/design.md#volume-state-is-existence). Mark* and a request's report write CurrentExistence;
    // a request, and a report that it failed, write DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current).
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public string? ExternalId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// Billed while Incus holds it, a delete in flight included (titan-ops features/volume/design.md#counting-and-billing).
    /// The repository lists with it in SQL.
    public static Expression<Func<VolumeSnapshot, bool>> IsBilled { get; } = s => s.CurrentExistence == CurrentExistence.Exists;

    private VolumeSnapshot() { }

    /// The move the waiting request makes, for display.
    public VolumeSnapshotTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case VolumeSnapshotRequestKind.Create:
                    return VolumeSnapshotTransition.Creating;
                case VolumeSnapshotRequestKind.Delete:
                    return VolumeSnapshotTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingRequestKind == VolumeSnapshotRequestKind.Create;
    private bool DeleteIsWaiting => PendingRequestKind == VolumeSnapshotRequestKind.Delete;
    /// A delete was asked and still waits, or the snapshot is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsTerminal;

    /// Counted by occupancy toward MaxVolumeSnapshotsPerVolume, and the same count says whether the volume may be
    /// deleted (titan-ops features/volume/design.md#counting-and-billing): what Incus holds or is about to hold. A delete
    /// asked for stops counting at once; a failed create with nothing on Incus, and a snapshot Incus lost, do not count.
    public bool CountsTowardQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && CreateIsWaiting));

    /// Called by Volume.AddSnapshot after the volume checked itself.
    internal static VolumeSnapshot Create(Guid volumeId, Guid tenantId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > NameMaxLength)
        {
            throw new BusinessException("INVALID_SNAPSHOT_NAME", $"A snapshot name is 1 to {NameMaxLength} characters, not blank.");
        }
        var snapshot = new VolumeSnapshot
        {
            Id = Guid.NewGuid(),
            VolumeId = volumeId,
            TenantId = tenantId,
            Name = name,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            CreatedAt = DateTimeOffset.UtcNow
        };
        snapshot.SetPending(NewRequest(VolumeSnapshotRequestKind.Create));
        return snapshot;
    }

    /// Deleted is absorbing (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    /// Incus holds it and nothing waits: the only situation a snapshot can be restored from.
    private bool RestsOnIncus =>
        PendingRequestId == null
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
    internal RequestTicket? RequestDelete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsTerminal)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && PendingRequestId == null;
        if (neverReachedIncus)
        {
            DesiredExistence = DesiredExistence.Deleted;
            CurrentExistence = CurrentExistence.Deleted;
            SetPending(null);
            return null;
        }
        if (CreateIsWaiting || ExternalId is null)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot is not yet on Incus.");
        PendingRequest<VolumeSnapshotRequestKind> request = NewRequest(VolumeSnapshotRequestKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(request);
        return RequestTicket.Of(request.Id, ExternalId);
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
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");

    // ---- The report of the snapshot's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends its request.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null. Done of the
    /// create records the id and existence, Done of the delete ends the snapshot; Failed records the reason, keeps an
    /// id the worker found, and gives a delete back (titan-ops architecture/resource-lifecycle.md#kinds).
    internal PendingRequest<VolumeSnapshotRequestKind>? EndRequest(Guid requestId, VolumeSnapshotRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<VolumeSnapshotRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }
        RequireProviderIdOnDone(end.Outcome, ExternalId, end.ExternalId);

        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }

        if (end.Outcome == RequestOutcome.Done)
        {
            ClearFailure();
            if (waiting!.Kind == VolumeSnapshotRequestKind.Delete)
            {
                CurrentExistence = CurrentExistence.Deleted;
            }
        }
        else
        {
            RecordFailure(end.Reason, end.Detail);
            if (waiting!.Kind == VolumeSnapshotRequestKind.Delete)
            {
                DesiredExistence = DesiredExistence.Exists;
            }
        }

        SetPending(null);
        return waiting;
    }

    /// Incus deleted the volume, and with it this snapshot: the volume's delete Done ends it, whatever it waited on.
    /// Returns true when the snapshot's own delete was waiting, so the caller records its outcome once.
    internal bool EndWithItsVolume()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool itsOwnDeleteWasWaiting = DeleteIsWaiting;
        CurrentExistence = CurrentExistence.Deleted;
        SetPending(null);
        return itsOwnDeleteWasWaiting;
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, never throws,
    // ends no request (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler found the snapshot on Incus.
    internal void MarkObserved()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler did not find the snapshot, or its volume. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
    }
}

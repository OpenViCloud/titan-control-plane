using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Compute.VolumeSnapshot;

public class VolumeSnapshot : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid VolumeId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    // Incus has no status word for a volume snapshot, only whether it is there
    // (titan-ops features/volume/design.md#volume-state-is-existence). Only Mark* writes CurrentExistence; only a request
    // writes DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current).
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public Guid? PendingIntentId { get; private set; }
    public string? ExternalId { get; private set; }
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private VolumeSnapshot() { }

    /// Derived, never stored.
    public VolumeSnapshotTransition? Transition
    {
        get
        {
            bool nothingIsWaiting = PendingIntentId == null;
            if (nothingIsWaiting)
            {
                return null;
            }
            if (DesiredExistence == DesiredExistence.Deleted)
            {
                return VolumeSnapshotTransition.Deleting;
            }
            return VolumeSnapshotTransition.Creating;
        }
    }

    private bool CreateIsWaiting => PendingIntentId != null && DesiredExistence == DesiredExistence.Exists;
    private bool DeleteIsWaiting => PendingIntentId != null && DesiredExistence == DesiredExistence.Deleted;

    /// Takes the Volume, not its id, so the volume rule is checked here
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation); quota too
    /// (titan-ops architecture/code-structure.md#tenant-typed-edge). The cap is per volume: the count is on THIS volume.
    public static VolumeSnapshot Create(Volume.Volume volume, Tenant tenant, int activeSnapshotCount, string name)
    {
        volume.EnsureSnapshottable();
        tenant.EnsureCanCreateVolumeSnapshot(activeSnapshotCount);
        return CreateUnchecked(volume.Id, tenant.Id, name);
    }

    /// Test seam: seeding needs a known parent id, which the checked factory takes from the Volume it validates.
    /// `internal`, so only Titan.Tests reaches it (titan-ops architecture/code-structure.md#tenant-typed-edge).
    internal static VolumeSnapshot CreateUnchecked(Guid volumeId, Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(),
        VolumeId = volumeId,
        TenantId = tenantId,
        Name = name,
        CurrentExistence = CurrentExistence.NotCreated,
        DesiredExistence = DesiredExistence.Exists,
        PendingIntentId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow
    };

    /// Deleted is absorbing (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    /// Incus holds it and nothing waits: the only situation a snapshot can be restored from.
    private bool RestsOnIncus =>
        PendingIntentId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// Restore creates a new volume and leaves this snapshot alone, so the check is called from
    /// Volume.CreateFromSnapshot and BeginRestore, never from a handler.
    internal void EnsureRestorable()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to restore.");
    }

    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public string RequestDelete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsTerminal)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        if (CreateIsWaiting || ExternalId is null)
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot is not yet on Incus.");
        DesiredExistence = DesiredExistence.Deleted;
        PendingIntentId = Guid.NewGuid();
        return ExternalId;
    }

    /// The delete command gave up (titan-ops architecture/desired-and-current.md#abandoned-request): the snapshot is still
    /// on Incus, so DesiredExistence returns to Exists, the reason is kept, and the next delete is a new request.
    public void AbandonRequest(string? reason, string? detail)
    {
        if (!DeleteIsWaiting)
        {
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "No delete is waiting on this snapshot.");
        }

        DesiredExistence = DesiredExistence.Exists;
        PendingIntentId = null;
        RecordFailure(reason, detail);
    }

    /// Intent: a snapshot that never reached Incus has no command and no observation coming, so this writes the
    /// existence itself (titan-ops architecture/desired-and-current.md#delete-locally).
    public void DeleteLocally()
    {
        bool neverReachedIncus = ExternalId is null && !CreateIsWaiting && !IsTerminal;
        if (!neverReachedIncus)
        {
            throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE",
                "Only a snapshot whose create failed before Incus had it can be deleted locally.");
        }
        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        PendingIntentId = null;
    }

    /// Volume.CreateFromSnapshot checks the same rule; this hands out the id, and the caller needs both,
    /// so neither can be skipped.
    public string BeginRestore()
    {
        EnsureRestorable();
        return RequireExternalId();
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public for the storage
    /// reconciler's read model.
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VOLUME_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): converge, never throw.
    // Each returns true when the report fulfilled the waiting request, so the callback records its outcome once.

    /// The create command: Incus made the snapshot before answering, so this is the create done. A repeat keeps the
    /// first id.
    public bool MarkCreated(string externalId)
    {
        if (IsTerminal)
        {
            return false;
        }
        if (ExternalId == null)
        {
            ExternalId = externalId;
        }
        return MarkObserved();
    }

    /// The reconciler found the snapshot on Incus. Ends a waiting create whose Created report was lost; no
    /// observation withdraws a waiting delete.
    public bool MarkObserved()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool createWasWaiting = CreateIsWaiting;
        CurrentExistence = CurrentExistence.Exists;
        if (!createWasWaiting)
        {
            return false;
        }
        PendingIntentId = null;
        return true;
    }

    /// The reconciler did not find the snapshot. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis). Ends a waiting create; a waiting delete keeps waiting.
    public void MarkNotFound()
    {
        if (IsTerminal)
        {
            return;
        }
        CurrentExistence = CurrentExistence.NotFound;
        if (CreateIsWaiting)
        {
            PendingIntentId = null;
        }
    }

    /// The delete command (or the parent volume's) confirmed it gone. Idempotent, never refuses.
    public bool MarkDeleted()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool fulfilled = DeleteIsWaiting;
        CurrentExistence = CurrentExistence.Deleted;
        PendingIntentId = null;
        return fulfilled;
    }

    /// The create command gave up (titan-ops architecture/desired-and-current.md#request-failure): records why and ends the
    /// waiting create. Stale with no create waiting; a delete's give-up is AbandonRequest.
    public void MarkRequestFailed(string? reason, string? detail)
    {
        if (IsTerminal || !CreateIsWaiting)
        {
            return;
        }
        PendingIntentId = null;
        RecordFailure(reason, detail);
    }

    private void RecordFailure(string? reason, string? detail)
    {
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
    }
}

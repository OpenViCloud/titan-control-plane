using System.Linq.Expressions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

public class Volume : AggregateRoot
{
    /// A volume's name: 1 to 100 characters, not blank, any characters. The validators read it.
    public const int NameMaxLength = 100;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public string VolumeOfferingId { get; private set; } = default!;
    public int SizeGb { get; private set; }
    // Incus has no status word for a custom volume: it is there or not, and used by instances or not
    // (titan-ops features/volume/design.md#volume-state-is-existence). Mark* and a request's report write the current pair
    // (existence and attachment); a request, and a report that it failed, write the desired pair
    // (titan-ops architecture/desired-and-current.md#desired-and-current).
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    /// The Titan VM Incus reports using this volume, or null.
    public Guid? CurrentAttachedVmId { get; private set; }
    /// An instance that is not a Titan VM of this tenant uses it (attached outside Titan).
    public bool CurrentAttachedElsewhere { get; private set; }
    /// The VM the customer asked this volume to be attached to, or null.
    public Guid? DesiredAttachedVmId { get; private set; }
    public string? ExternalId { get; private set; }
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // The one waiting request, as three columns (titan-ops architecture/resource-lifecycle.md#shape); all null or all
    // set, written by the factory and SetPending. Pending is the shape callers see.
    private Guid? PendingRequestId { get; set; }
    private VolumeRequestKind? PendingRequestKind { get; set; }
    private DateTimeOffset? PendingRequestAskedAt { get; set; }

    /// The request this volume is waiting on, or null. Only the report carrying its id ends it
    /// (titan-ops architecture/resource-lifecycle.md#invariants).
    public PendingRequest<VolumeRequestKind>? Pending =>
        PendingRequestId == null
            ? null
            : PendingRequest<VolumeRequestKind>.Of(PendingRequestId.Value, PendingRequestKind!.Value, PendingRequestAskedAt!.Value);

    /// The volume's snapshots, each a copy on Incus named under the volume; rows are kept after Deleted
    /// (titan-ops features/volume/design.md#volume-snapshot). The repository loads them with the volume.
    private readonly List<VolumeSnapshot> _snapshots = [];
    public IReadOnlyCollection<VolumeSnapshot> Snapshots => _snapshots;

    /// What counts toward MaxVolumeSnapshotsPerVolume, and what refuses the volume's delete
    /// (titan-ops features/volume/design.md#counting-and-billing).
    public int SnapshotQuotaUsage => _snapshots.Count(s => s.CountsTowardQuota);

    /// Counts toward MaxVolumes (titan-ops features/volume/design.md#counting-and-billing): what Incus holds or is about
    /// to, unless its delete was pressed. A create that failed before Incus had it, or a volume Incus no longer has, does
    /// not count. Written once here; the repository counts with it in SQL.
    public static Expression<Func<Volume, bool>> OccupiesQuota { get; } = v =>
        v.DesiredExistence == DesiredExistence.Exists
        && (v.CurrentExistence == CurrentExistence.Exists
            || (v.CurrentExistence == CurrentExistence.NotCreated && v.PendingRequestId != null));

    /// Billed while Incus holds it, a delete in flight included (titan-ops features/volume/design.md#counting-and-billing).
    public static Expression<Func<Volume, bool>> IsBilled { get; } = v => v.CurrentExistence == CurrentExistence.Exists;

    private Volume() { }

    /// The move the waiting request makes, for display. Null with the desired attachment differing from the current one
    /// is drift.
    public VolumeTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case VolumeRequestKind.Create:
                    return VolumeTransition.Creating;
                case VolumeRequestKind.Attach:
                    return VolumeTransition.Attaching;
                case VolumeRequestKind.Detach:
                    return VolumeTransition.Detaching;
                case VolumeRequestKind.Delete:
                    return VolumeTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingRequestKind == VolumeRequestKind.Create;
    private bool DeleteIsWaiting => PendingRequestKind == VolumeRequestKind.Delete;

    /// Incus reports an instance using it, a Titan VM or not.
    private bool IsInUse => CurrentAttachedVmId != null || CurrentAttachedElsewhere;

    /// Incus holds it and nothing waits. Every intent but delete starts from here.
    private bool RestsOnIncus =>
        PendingRequestId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    /// A volume restored from a snapshot: the snapshot's readiness is an invariant of the creation (titan-ops features/volume/design.md#volume-restore-is-a-new-volume);
    /// sizing and cluster come from the source volume, which the caller resolves.
    public static Volume Create(
        Guid tenantId,
        string name, string clusterId, string volumeOfferingId, int sizeGb,
        VolumeSnapshot? restoredFrom = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > NameMaxLength)
        {
            throw new BusinessException("INVALID_VOLUME_NAME", $"A volume name is 1 to {NameMaxLength} characters, not blank.");
        }
        if (sizeGb <= 0)
        {
            throw new BusinessException("INVALID_VOLUME_SIZE", "A volume is at least 1 GB.");
        }
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
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            CreatedAt = DateTimeOffset.UtcNow
        };
        volume.SetPending(NewRequest(VolumeRequestKind.Create));
        return volume;
    }

    private static PendingRequest<VolumeRequestKind> NewRequest(VolumeRequestKind kind) =>
        PendingRequest<VolumeRequestKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);

    private void SetPending(PendingRequest<VolumeRequestKind>? request)
    {
        PendingRequestId = request?.Id;
        PendingRequestKind = request?.Kind;
        PendingRequestAskedAt = request?.AskedAt;
    }

    /// Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// Checks the volume, the VM (Vm.ProviderIdForVolumeAttach) and the cluster; the provider ids come out only past all
    /// three (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). The current attachment
    /// waits for Incus to be seen using the volume.
    public VolumeAttachProviderIds Attach(Vm.Vm vm)
    {
        bool available = RestsOnIncus && !IsInUse;
        if (!available)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, unattached, with nothing waiting, to attach.");
        var vmExternalId = vm.ProviderIdForVolumeAttach();
        if (ClusterId != vm.ClusterId)
            throw new BusinessException("VOLUME_CLUSTER_MISMATCH", "Volume and VM must be in the same cluster.");

        string externalVolumeId = RequireExternalId();
        PendingRequest<VolumeRequestKind> request = NewRequest(VolumeRequestKind.Attach);
        DesiredAttachedVmId = vm.Id;
        SetPending(request);
        return new VolumeAttachProviderIds(request.Id, externalVolumeId, vmExternalId);
    }

    /// The detach command removes the disk from every instance using the volume, so a volume an instance outside
    /// Titan also uses is refused: Titan does not edit an instance it does not manage.
    public RequestTicket Detach()
    {
        bool onATitanVmOnly = RestsOnIncus && CurrentAttachedVmId != null && !CurrentAttachedElsewhere;
        if (!onATitanVmOnly)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be attached to a Titan VM only, with nothing waiting, to detach.");
        string externalVolumeId = RequireExternalId();
        PendingRequest<VolumeRequestKind> request = NewRequest(VolumeRequestKind.Detach);
        DesiredAttachedVmId = null;
        SetPending(request);
        return RequestTicket.Of(request.Id, externalVolumeId);
    }

    // ---- A VM's delete is not a request of the volume (titan-ops architecture/resource-lifecycle.md#kinds): the volume's
    // own waiting request, if any, is left to its own command's report.

    /// The VM this volume is asked to be attached to is being deleted, or its delete was done: Incus keeps the volume
    /// and detaches it when it deletes the VM (titan-ops features/volume/design.md#vm-delete-releases-volumes), so the
    /// attachment is no longer asked for. Only a volume with nothing waiting: an attach or detach of the customer's own
    /// keeps its desired attachment until its command reports.
    public void ReleaseFromVmDelete(Guid vmId)
    {
        bool askedForThatVmWithNothingWaiting = PendingRequestId == null && DesiredAttachedVmId == vmId && !IsTerminal;
        if (askedForThatVmWithNothingWaiting)
        {
            DesiredAttachedVmId = null;
        }
    }

    /// The delete of the VM using this volume gave up: the VM stays, and so does the attachment its delete released.
    /// Only a volume with nothing waiting, released by that delete (nothing asked, Incus still shows that VM).
    public void KeepAttachmentAfterVmDeleteFailed(Guid vmId)
    {
        bool releasedByThatDelete = PendingRequestId == null && DesiredAttachedVmId == null && CurrentAttachedVmId == vmId
                                    && !IsTerminal;
        if (releasedByThatDelete)
        {
            DesiredAttachedVmId = vmId;
        }
    }

    /// Follows Incus: a volume with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat), and refuses while a
    /// snapshot it holds still counts: Incus deletes a volume's snapshots with it.
    public RequestTicket RequestDelete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VOLUME_INVALID_STATE", "A delete is already in progress for this volume.");
        if (IsTerminal)
            throw new BusinessException("VOLUME_INVALID_STATE", "The volume is already deleted.");
        if (PendingRequestId != null || ExternalId is null)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to delete.");
        if (IsInUse)
            throw new BusinessException("VOLUME_INVALID_STATE", "Detach the volume before deleting it.");
        // Deleting a volume deletes its snapshots too (titan-ops features/volume/design.md#delete-refused-with-snapshots).
        bool hasLiveSnapshots = SnapshotQuotaUsage > 0;
        if (hasLiveSnapshots)
            throw new BusinessException("VOLUME_HAS_SNAPSHOTS", "Delete the volume's snapshots before the volume.");
        PendingRequest<VolumeRequestKind> request = NewRequest(VolumeRequestKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(request);
        return RequestTicket.Of(request.Id, ExternalId);
    }

    /// Intent: a volume that never reached Incus has no command and no observation coming, so this writes the
    /// existence itself (titan-ops architecture/desired-and-current.md#delete-locally).
    public void DeleteLocally()
    {
        bool neverReachedIncus = ExternalId is null && !CreateIsWaiting && !IsTerminal;
        if (!neverReachedIncus)
        {
            throw new BusinessException("VOLUME_INVALID_STATE",
                "Only a volume whose create failed before Incus had it can be deleted locally.");
        }
        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        SetPending(null);
    }

    // ---- Snapshots: the volume counts its snapshots and routes each call to the snapshot it names
    // (titan-ops architecture/code-structure.md#child-entity). Logic stays on VolumeSnapshot; this is the way in.

    /// Intent: a new snapshot of this volume. The volume must rest on Incus (EnsureSnapshottable). The create request
    /// is the snapshot's own Pending.
    public VolumeSnapshotTicket AddSnapshot(string name)
    {
        EnsureSnapshottable();
        VolumeSnapshot snapshot = VolumeSnapshot.Create(Id, TenantId, name);
        _snapshots.Add(snapshot);
        return new VolumeSnapshotTicket(snapshot, RequireExternalId());
    }

    /// A snapshot this volume does not have answers like a missing one, the same 404 a snapshot of another volume gets
    /// (titan-ops features/volume/design.md#volume-snapshot).
    private VolumeSnapshot RequireSnapshot(Guid snapshotId)
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

    /// Intent on one snapshot: its delete goes through Incus; returns the snapshot's provider id for the command.
    public RequestTicket RequestSnapshotDelete(Guid snapshotId) => RequireSnapshot(snapshotId).RequestDelete();

    /// Intent on one snapshot whose create failed before Incus had it (titan-ops architecture/desired-and-current.md#delete-locally).
    public void DeleteSnapshotLocally(Guid snapshotId) => RequireSnapshot(snapshotId).DeleteLocally();

    /// A restore starts from a snapshot resting on Incus and hands out its provider id
    /// (titan-ops features/volume/design.md#volume-restore-is-a-new-volume); the new volume is Create's.
    public string ProviderIdForSnapshotRestore(Guid snapshotId) => RequireSnapshot(snapshotId).ProviderIdForRestore();

    /// The report of the snapshot's own command (titan-ops architecture/resource-lifecycle.md#shape); null when it names
    /// another request than the one the snapshot waits on.
    public PendingRequest<VolumeSnapshotRequestKind>? EndSnapshotRequest(Guid snapshotId, Guid requestId, VolumeSnapshotRequestEnd end) =>
        RequireSnapshot(snapshotId).EndRequest(requestId, end);

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkSnapshotObserved(Guid snapshotId) => RequireSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => RequireSnapshot(snapshotId).MarkNotFound();

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    private string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VOLUME_INVALID_STATE", "Volume has no provider resource yet.");

    /// The precondition of AddSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to create a snapshot.");
    }

    // ---- The report of a request's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // a request. It writes both axes — what the worker saw is current, and a failure puts desired back — so it is
    // neither an intent nor a Mark*.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null: a stale
    /// give-up, a Done that arrived twice, a report for a volume already Deleted. Done and Failed both record what the
    /// worker saw. Done gives the request its effect and clears an older failure; Failed records the reason and puts
    /// desired back to current according to the kind (titan-ops architecture/resource-lifecycle.md#kinds).
    public VolumeRequestEnded? EndRequest(Guid requestId, VolumeRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<VolumeRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }

        // What the worker saw, whatever the outcome: a create that failed after Incus made the volume leaves a volume
        // whose delete still reaches Incus.
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }
        if (end.Observed != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentAttachedVmId = end.Observed.AttachedVmId;
            CurrentAttachedElsewhere = end.Observed.AttachedElsewhere;
        }

        var snapshotsWhoseDeleteEnded = new List<VolumeSnapshot>();
        if (end.Outcome == RequestOutcome.Done)
        {
            RequestFailureReason = null;
            RequestFailureDetail = null;
            RequestFailedAt = null;
            if (waiting!.Kind == VolumeRequestKind.Delete)
            {
                // Incus deletes a volume's snapshots with it (titan-ops features/volume/design.md#delete-refused-with-snapshots):
                // every snapshot not yet Deleted goes with the volume; the ones whose own delete was waiting are named
                // for their outcome.
                CurrentExistence = CurrentExistence.Deleted;
                CurrentAttachedVmId = null;
                CurrentAttachedElsewhere = false;
                foreach (VolumeSnapshot snapshot in _snapshots)
                {
                    bool itsOwnDeleteWasWaiting = snapshot.EndWithItsVolume();
                    if (itsOwnDeleteWasWaiting)
                    {
                        snapshotsWhoseDeleteEnded.Add(snapshot);
                    }
                }
            }
        }
        else
        {
            RequestFailureReason = end.Reason;
            RequestFailureDetail = end.Detail;
            RequestFailedAt = DateTimeOffset.UtcNow;
            switch (waiting!.Kind)
            {
                case VolumeRequestKind.Delete:
                    DesiredExistence = DesiredExistence.Exists;
                    break;
                case VolumeRequestKind.Attach:
                case VolumeRequestKind.Detach:
                    DesiredAttachedVmId = CurrentAttachedVmId;
                    break;
                case VolumeRequestKind.Create:
                    // A failed create may be deleted, not retried: desired stays, the reason says why it is not so.
                    break;
            }
        }

        SetPending(null);
        return new VolumeRequestEnded(waiting, snapshotsWhoseDeleteEnded);
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what Incus has, so
    // no guard, and no request ends here (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the volume on Incus: the Titan VM using it, and whether an instance outside Titan does.
    public void MarkObserved(Guid? attachedVmId, bool attachedElsewhere)
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentAttachedVmId = attachedVmId;
        CurrentAttachedElsewhere = attachedElsewhere;
    }

    /// The delete command of the VM using this volume confirmed the instance gone: Incus keeps the volume and drops
    /// the attachment (titan-ops features/volume/design.md#vm-delete-releases-volumes).
    public void MarkDetachedByVmDelete(Guid vmId)
    {
        if (IsTerminal) return;
        if (CurrentAttachedVmId == vmId)
        {
            CurrentAttachedVmId = null;
        }
    }

    /// The reconciler did not find the volume. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentAttachedVmId = null;
        CurrentAttachedElsewhere = false;
    }
}

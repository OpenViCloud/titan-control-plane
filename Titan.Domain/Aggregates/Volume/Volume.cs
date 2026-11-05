using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Volume;

public class Volume : ProviderResource<VolumeOperationKind>, IAggregateRoot
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
    // (titan-ops features/volume/design.md#volume-state-is-existence). Mark* and an operation's report write the current pair
    // (existence and attachment); an operation, and a report that it failed, write the desired pair
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
    public DateTimeOffset CreatedAt { get; private set; }

    /// The volume's snapshots, each a copy on Incus named under the volume; rows are kept after Deleted
    /// (titan-ops features/volume/design.md#volume-snapshot). The repository loads them with the volume.
    private readonly List<VolumeSnapshot> _snapshots = [];
    public IReadOnlyCollection<VolumeSnapshot> Snapshots => _snapshots;

    /// What counts toward MaxVolumeSnapshotsPerVolume, and what refuses the volume's delete
    /// (titan-ops features/volume/design.md#counting-and-billing).
    public int SnapshotQuotaUsage => _snapshots.Count(s => s.CountsTowardQuota);

    /// Counts toward MaxVolumes (titan-ops features/volume/design.md#counting-and-billing): what Incus holds or is about
    /// to, unless its delete was pressed. A create that failed before Incus had it, or a volume Incus no longer has, does
    /// not count. VolumeRepository.CountActiveByTenantAsync writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool OccupiesQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && PendingOperationId != null));

    /// Billed while Incus holds it, a delete in flight included (titan-ops features/volume/design.md#counting-and-billing).
    /// VolumeRepository.ListBillableAsync writes the same test in SQL.
    public bool IsBilled => CurrentExistence == CurrentExistence.Exists;

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// Until then the volume is still listed, still goes with its VM's delete and is still observed; the repository's
    /// queries write !IsDeleted in SQL, the raw SQL that locks the attached volumes included
    /// (ListAttachedToVmLockedAsync; titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => CurrentExistence == CurrentExistence.Deleted;

    private Volume() { }

    /// The move the waiting operation makes, for display. Null with the desired attachment differing from the current one
    /// is drift.
    public VolumeTransition? Transition
    {
        get
        {
            switch (PendingOperationKind)
            {
                case VolumeOperationKind.Create:
                    return VolumeTransition.Creating;
                case VolumeOperationKind.Attach:
                    return VolumeTransition.Attaching;
                case VolumeOperationKind.Detach:
                    return VolumeTransition.Detaching;
                case VolumeOperationKind.Delete:
                    return VolumeTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingOperationKind == VolumeOperationKind.Create;
    private bool DeleteIsWaiting => PendingOperationKind == VolumeOperationKind.Delete;
    /// A delete was asked and still waits, or the volume is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsDeleted;

    /// Incus reports an instance using it, a Titan VM or not.
    private bool IsInUse => CurrentAttachedVmId != null || CurrentAttachedElsewhere;

    /// Incus holds it and nothing waits. Every intent but delete starts from here.
    private bool RestsOnIncus =>
        PendingOperationId == null
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
        volume.SetPending(NewOperation(VolumeOperationKind.Create));
        return volume;
    }

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

        string externalVolumeId = GetExternalId();
        PendingOperation<VolumeOperationKind> operation = NewOperation(VolumeOperationKind.Attach);
        DesiredAttachedVmId = vm.Id;
        SetPending(operation);
        return new VolumeAttachProviderIds(operation.Id, externalVolumeId, vmExternalId);
    }

    /// The detach command removes the disk from every instance using the volume, so a volume an instance outside
    /// Titan also uses is refused: Titan does not edit an instance it does not manage.
    public OperationTicket Detach()
    {
        bool onATitanVmOnly = RestsOnIncus && CurrentAttachedVmId != null && !CurrentAttachedElsewhere;
        if (!onATitanVmOnly)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be attached to a Titan VM only, with nothing waiting, to detach.");
        string externalVolumeId = GetExternalId();
        PendingOperation<VolumeOperationKind> operation = NewOperation(VolumeOperationKind.Detach);
        DesiredAttachedVmId = null;
        SetPending(operation);
        return OperationTicket.Of(operation.Id, externalVolumeId);
    }

    // ---- A VM's delete is not an operation of the volume (titan-ops architecture/resource-lifecycle.md#kinds): the volume's
    // own waiting operation, if any, is left to its own command's report.

    /// The VM this volume is asked to be attached to is being deleted, or its delete was done: Incus keeps the volume
    /// and detaches it when it deletes the VM (titan-ops features/volume/design.md#vm-delete-releases-volumes), so the
    /// attachment is no longer asked for. Only a volume with nothing waiting: an attach or detach of the customer's own
    /// keeps its desired attachment until its command reports.
    public void ReleaseFromVmDelete(Guid vmId)
    {
        bool askedForThatVmWithNothingWaiting = PendingOperationId == null && DesiredAttachedVmId == vmId && !IsDeleted;
        if (askedForThatVmWithNothingWaiting)
        {
            DesiredAttachedVmId = null;
        }
    }

    /// The delete of the VM using this volume gave up: the VM stays, and so does the attachment its delete released.
    /// Only a volume with nothing waiting, released by that delete (nothing asked, Incus still shows that VM).
    public void KeepAttachmentAfterVmDeleteFailed(Guid vmId)
    {
        bool releasedByThatDelete = PendingOperationId == null && DesiredAttachedVmId == null && CurrentAttachedVmId == vmId
                                    && !IsDeleted;
        if (releasedByThatDelete)
        {
            DesiredAttachedVmId = vmId;
        }
    }

    /// Follows Incus: a volume with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat), and refuses while a
    /// snapshot it holds still counts: Incus deletes a volume's snapshots with it.
    /// A volume that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    public OperationTicket? Delete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VOLUME_INVALID_STATE", "A delete is already in progress for this volume.");
        if (IsDeleted)
            throw new BusinessException("VOLUME_INVALID_STATE", "The volume is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && PendingOperationId == null;
        if (neverReachedIncus)
        {
            DesiredExistence = DesiredExistence.Deleted;
            CurrentExistence = CurrentExistence.Deleted;
            SetPending(null);
            return null;
        }
        if (PendingOperationId != null || ExternalId is null)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to delete.");
        if (IsInUse)
            throw new BusinessException("VOLUME_INVALID_STATE", "Detach the volume before deleting it.");
        // Deleting a volume deletes its snapshots too (titan-ops features/volume/design.md#delete-refused-with-snapshots).
        bool hasLiveSnapshots = SnapshotQuotaUsage > 0;
        if (hasLiveSnapshots)
            throw new BusinessException("VOLUME_HAS_SNAPSHOTS", "Delete the volume's snapshots before the volume.");
        PendingOperation<VolumeOperationKind> operation = NewOperation(VolumeOperationKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(operation);
        return OperationTicket.Of(operation.Id, ExternalId);
    }

    // ---- Snapshots: the volume counts its snapshots and routes each call to the snapshot it names
    // (titan-ops architecture/code-structure.md#child-entity). Logic stays on VolumeSnapshot; this is the way in.

    /// Intent: a new snapshot of this volume, with the id the handler chose. The volume must rest on Incus
    /// (EnsureSnapshottable). The create operation is the snapshot's own Pending; its ticket carries the volume's
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

    /// The report that the snapshot's own command ended Done (titan-ops architecture/resource-lifecycle.md#shape); null
    /// when it names another operation than the one the snapshot waits on.
    public VolumeSnapshotOperationKind? FinishSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId) =>
        GetSnapshot(snapshotId).FinishOperation(operationId, externalId);

    /// The report that the snapshot's own command gave up; null when it names another operation than the one the
    /// snapshot waits on.
    public VolumeSnapshotOperationKind? FailSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId,
        string? reason, string? detail) =>
        GetSnapshot(snapshotId).FailOperation(operationId, externalId, reason, detail);

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkSnapshotObserved(Guid snapshotId) => GetSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => GetSnapshot(snapshotId).MarkNotFound();

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    private string GetExternalId()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume has no provider resource yet.");
        }
        return ExternalId;
    }

    /// The precondition of AddSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to create a snapshot.");
    }

    // ---- The report of an operation's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // an operation. It writes both axes — what the worker saw is current, and a failure puts desired back — so it is
    // neither an intent nor a Mark*.

    /// The command ended Done. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null: a Done that arrived twice, a report for a volume already Deleted. Records what the worker saw,
    /// gives the operation its effect and clears an older failure (titan-ops architecture/resource-lifecycle.md#kinds).
    /// sawTheVolume false: the worker did not read the volume on Incus, so the attachment is not read either.
    public VolumeOperationKind? FinishOperation(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere)
    {
        if (IsDeleted) return null;
        PendingOperation<VolumeOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RequireProviderIdOnDone(ExternalId, externalId);
        RecordWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);

        ClearFailure();
        if (waiting.Kind == VolumeOperationKind.Delete)
        {
            // Incus deletes a volume's snapshots with it (titan-ops features/volume/design.md#delete-refused-with-snapshots):
            // every snapshot not yet Deleted goes with the volume.
            CurrentExistence = CurrentExistence.Deleted;
            CurrentAttachedVmId = null;
            CurrentAttachedElsewhere = false;
            foreach (VolumeSnapshot snapshot in _snapshots)
            {
                snapshot.EndWithItsVolume();
            }
        }

        SetPending(null);
        return waiting.Kind;
    }

    /// The command gave up. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null: a stale give-up, a report for a volume already Deleted. Records what the worker saw and the
    /// reason, and puts desired back to current according to the kind (titan-ops architecture/resource-lifecycle.md#kinds).
    /// sawTheVolume false: the worker did not read the volume on Incus, so the attachment is not read either.
    public VolumeOperationKind? FailOperation(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere, string? reason, string? detail)
    {
        if (IsDeleted) return null;
        PendingOperation<VolumeOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RecordWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);

        RecordFailure(reason, detail);
        switch (waiting.Kind)
        {
            case VolumeOperationKind.Delete:
                DesiredExistence = DesiredExistence.Exists;
                break;
            case VolumeOperationKind.Attach:
            case VolumeOperationKind.Detach:
                DesiredAttachedVmId = CurrentAttachedVmId;
                break;
            case VolumeOperationKind.Create:
                // A failed create may be deleted, not retried: desired stays, the reason says why it is not so.
                break;
        }

        SetPending(null);
        return waiting.Kind;
    }

    private PendingOperation<VolumeOperationKind>? WaitingOperationNamed(Guid operationId)
    {
        PendingOperation<VolumeOperationKind>? waiting = Pending;
        bool reportIsForThePendingOperation = waiting != null && waiting.Id == operationId;
        if (!reportIsForThePendingOperation)
        {
            return null;
        }
        return waiting;
    }

    // What the worker saw, whatever the outcome: a create that failed after Incus made the volume leaves a volume whose
    // delete still reaches Incus.
    private void RecordWhatTheWorkerSaw(string? externalId, bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
            CurrentExistence = CurrentExistence.Exists;
        }
        if (sawTheVolume)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentAttachedVmId = attachedVmId;
            CurrentAttachedElsewhere = attachedElsewhere;
        }
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what Incus has, so
    // no guard, and no operation ends here (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the volume on Incus: the Titan VM using it, and whether an instance outside Titan does.
    public void MarkObserved(Guid? attachedVmId, bool attachedElsewhere)
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentAttachedVmId = attachedVmId;
        CurrentAttachedElsewhere = attachedElsewhere;
    }

    /// The delete command of the VM using this volume confirmed the instance gone: Incus keeps the volume and drops
    /// the attachment (titan-ops features/volume/design.md#vm-delete-releases-volumes).
    public void MarkDetachedByVmDelete(Guid vmId)
    {
        if (IsDeleted) return;
        if (CurrentAttachedVmId == vmId)
        {
            CurrentAttachedVmId = null;
        }
    }

    /// The reconciler did not find the volume. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentAttachedVmId = null;
        CurrentAttachedElsewhere = false;
    }
}

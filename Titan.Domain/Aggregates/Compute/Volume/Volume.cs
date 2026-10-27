using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Compute.Volume;

public class Volume
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string ZoneId { get; private set; } = default!;
    public string VolumeOfferingId { get; private set; } = default!;
    public int SizeGb { get; private set; }
    // Incus has no status word for a custom volume: it is there or not, and used by instances or not
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#volume-state-is-existence). Only Mark* writes the current pair (existence and
    // attachment); only a request writes the desired pair (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#desired-and-current).
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    /// The Titan VM Incus reports using this volume, or null.
    public Guid? CurrentAttachedVmId { get; private set; }
    /// An instance that is not a Titan VM of this tenant uses it (attached outside Titan).
    public bool CurrentAttachedElsewhere { get; private set; }
    /// The VM the customer asked this volume to be attached to, or null.
    public Guid? DesiredAttachedVmId { get; private set; }
    public Guid? PendingIntentId { get; private set; }
    public string? ExternalId { get; private set; }
    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private Volume() { }

    /// Derived, never stored. Nothing waiting with the desired attachment differing from the current one is drift.
    public VolumeTransition? Transition
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
                return VolumeTransition.Deleting;
            }
            if (CurrentExistence == CurrentExistence.NotCreated)
            {
                return VolumeTransition.Creating;
            }
            if (DesiredAttachedVmId != null)
            {
                return VolumeTransition.Attaching;
            }
            return VolumeTransition.Detaching;
        }
    }

    private bool CreateIsWaiting => Transition == VolumeTransition.Creating;
    private bool DeleteIsWaiting => Transition == VolumeTransition.Deleting;
    private bool AttachOrDetachIsWaiting =>
        Transition == VolumeTransition.Attaching || Transition == VolumeTransition.Detaching;

    /// Incus reports an instance using it, a Titan VM or not.
    private bool IsInUse => CurrentAttachedVmId != null || CurrentAttachedElsewhere;

    /// Incus holds it and nothing waits. Every intent but delete starts from here.
    private bool RestsOnIncus =>
        PendingIntentId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    /// Quota is checked inside the factory (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#tenant-typed-edge).
    public static Volume Create(
        Tenant tenant, int activeVolumeCount,
        string name, string zoneId, string volumeOfferingId, int sizeGb)
    {
        tenant.EnsureCanCreateVolume(activeVolumeCount);
        return CreateUnchecked(tenant.Id, name, zoneId, volumeOfferingId, sizeGb);
    }

    /// Test seam — internal, reachable only from Titan.Tests. See Vm.CreateUnchecked.
    internal static Volume CreateUnchecked(Guid tenantId, string name, string zoneId, string volumeOfferingId, int sizeGb) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            ZoneId = zoneId,
            VolumeOfferingId = volumeOfferingId,
            SizeGb = sizeGb,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            PendingIntentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };

    /// Deleted absorbs (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    // ---- Intent (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-versus-observation).

    /// The snapshot's readiness is an invariant of this creation
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#volume-restore-is-a-new-volume). Sizing and zone come from the source
    /// volume, which the caller resolves.
    public static Volume CreateFromSnapshot(
        VolumeSnapshot.VolumeSnapshot snapshot, Tenant tenant, int activeVolumeCount, string name,
        string zoneId, string volumeOfferingId, int sizeGb)
    {
        snapshot.EnsureRestorable();
        // A restore spends volume quota, not snapshot quota: the thing being created is a volume.
        tenant.EnsureCanCreateVolume(activeVolumeCount);
        return CreateUnchecked(tenant.Id, name, zoneId, volumeOfferingId, sizeGb);
    }

    /// Checks the volume, the VM (Vm.BeginVolumeAttach) and the zone; the provider ids come out only past all
    /// three (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#aggregate-hands-out-provider-id). The current attachment
    /// waits for Incus to be seen using the volume.
    public VolumeAttachProviderIds Attach(Vm.Vm vm)
    {
        bool available = RestsOnIncus && !IsInUse;
        if (!available)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, unattached, with nothing waiting, to attach.");
        var vmExternalId = vm.BeginVolumeAttach();
        if (ZoneId != vm.ZoneId)
            throw new BusinessException("VOLUME_CLUSTER_MISMATCH", "Volume and VM must be in the same zone.");

        DesiredAttachedVmId = vm.Id;
        PendingIntentId = Guid.NewGuid();
        return new VolumeAttachProviderIds(RequireExternalId(), vmExternalId);
    }

    /// The detach command removes the disk from every instance using the volume, so a volume an instance outside
    /// Titan also uses is refused: Titan does not edit an instance it does not manage.
    public string Detach()
    {
        bool onATitanVmOnly = RestsOnIncus && CurrentAttachedVmId != null && !CurrentAttachedElsewhere;
        if (!onATitanVmOnly)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be attached to a Titan VM only, with nothing waiting, to detach.");
        DesiredAttachedVmId = null;
        PendingIntentId = Guid.NewGuid();
        return RequireExternalId();
    }

    /// The VM holding this volume is being deleted, and the attachment goes with it: Incus keeps the volume and
    /// detaches it when it deletes the VM (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#vm-delete-releases-volumes). The VM's
    /// Deleted report completes it through MarkDetachedByVmDelete.
    public void DetachWithDeletedVm()
    {
        bool askedToBeAttached = DesiredAttachedVmId != null && DesiredExistence == DesiredExistence.Exists && !IsTerminal;
        if (!askedToBeAttached)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume is not asked to be attached to a VM.");
        DesiredAttachedVmId = null;
        PendingIntentId = Guid.NewGuid();
    }

    /// Follows Incus: a volume with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat). liveSnapshotCount is the
    /// caller's count of this volume's snapshots that exist or are being made.
    public string RequestDelete(int liveSnapshotCount)
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VOLUME_INVALID_STATE", "A delete is already in progress for this volume.");
        if (IsTerminal)
            throw new BusinessException("VOLUME_INVALID_STATE", "The volume is already deleted.");
        if (PendingIntentId != null || ExternalId is null)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to delete.");
        if (IsInUse)
            throw new BusinessException("VOLUME_INVALID_STATE", "Detach the volume before deleting it.");
        // Deleting a volume deletes its snapshots too (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#delete-refused-with-snapshots).
        bool hasLiveSnapshots = liveSnapshotCount > 0;
        if (hasLiveSnapshots)
            throw new BusinessException("VOLUME_HAS_SNAPSHOTS", "Delete the volume's snapshots before the volume.");
        DesiredExistence = DesiredExistence.Deleted;
        PendingIntentId = Guid.NewGuid();
        return ExternalId;
    }

    /// An attach, a detach or a delete command gave up (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#abandoned-request):
    /// the desired pair moves back to what Incus was last seen to hold, the reason is kept, and the next press is a
    /// new request. Not a Mark*: it changes what was asked for.
    public void AbandonRequest(string? reason, string? detail)
    {
        if (DeleteIsWaiting)
        {
            DesiredExistence = DesiredExistence.Exists;
        }
        else if (AttachOrDetachIsWaiting)
        {
            DesiredAttachedVmId = CurrentAttachedVmId;
        }
        else
        {
            throw new BusinessException("VOLUME_INVALID_STATE", "No attach, detach or delete is waiting on this volume.");
        }
        PendingIntentId = null;
        RecordFailure(reason, detail);
    }

    /// Intent: a volume that never reached Incus has no command and no observation coming, so this writes the
    /// existence itself (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#delete-locally).
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
        PendingIntentId = null;
    }

    /// VolumeSnapshot.Create checks the same rule; this hands out the id, and the caller needs both,
    /// so neither can be skipped.
    public string BeginSnapshot()
    {
        EnsureSnapshottable();
        return RequireExternalId();
    }

    /// Throws rather than let a caller assert the id
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public for the storage
    /// reconciler's read model.
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VOLUME_INVALID_STATE", "Volume has no provider resource yet.");

    /// A cross-aggregate precondition for VolumeSnapshot.Create, never called by a handler
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#validate-inside-the-mutation).
    internal void EnsureSnapshottable()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, with nothing waiting, to create a snapshot.");
    }

    // ---- Observation (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#observation-converges): converge, never throw.

    /// The create (or restore) command: Incus made the volume before answering, so this is the create done. A repeat
    /// keeps the first id. Returns true when it fulfilled the waiting create.
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
        bool createWasWaiting = CreateIsWaiting;
        CurrentExistence = CurrentExistence.Exists;
        if (!createWasWaiting)
        {
            return false;
        }
        PendingIntentId = null;
        return true;
    }

    /// The reconciler, or an attach or detach command, read the volume on Incus: the Titan VM using it, and whether
    /// an instance outside Titan does. Fulfils a waiting create (its Created report was lost), or a waiting attach or
    /// detach once Incus shows what was asked; no observation withdraws a waiting request. Returns the request it
    /// fulfilled, so the caller records its outcome once.
    public VolumeTransition? MarkObserved(Guid? attachedVmId, bool attachedElsewhere)
    {
        if (IsTerminal)
        {
            return null;
        }
        VolumeTransition? waiting = Transition;
        CurrentExistence = CurrentExistence.Exists;
        CurrentAttachedVmId = attachedVmId;
        CurrentAttachedElsewhere = attachedElsewhere;

        bool fulfilled = false;
        if (waiting == VolumeTransition.Creating)
        {
            fulfilled = true;
        }
        else if (waiting == VolumeTransition.Attaching)
        {
            fulfilled = CurrentAttachedVmId == DesiredAttachedVmId;
        }
        else if (waiting == VolumeTransition.Detaching)
        {
            fulfilled = !IsInUse;
        }
        if (!fulfilled)
        {
            return null;
        }
        PendingIntentId = null;
        return waiting;
    }

    /// The delete command of the VM using this volume confirmed the instance gone: Incus keeps the volume and drops
    /// the attachment (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#vm-delete-releases-volumes). Fulfils the detach that VM's delete
    /// asked for.
    public void MarkDetachedByVmDelete(Guid vmId)
    {
        if (IsTerminal)
        {
            return;
        }
        if (CurrentAttachedVmId == vmId)
        {
            CurrentAttachedVmId = null;
        }
        bool detachIsFulfilled = Transition == VolumeTransition.Detaching && !IsInUse;
        if (detachIsFulfilled)
        {
            PendingIntentId = null;
        }
    }

    /// The reconciler did not find the volume. Never Deleted: only Titan's own delete command confirms that
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#existence-axis). Ends a waiting create; every other request keeps
    /// waiting for its command.
    public void MarkNotFound()
    {
        if (IsTerminal)
        {
            return;
        }
        bool createWasWaiting = CreateIsWaiting;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentAttachedVmId = null;
        CurrentAttachedElsewhere = false;
        if (createWasWaiting)
        {
            PendingIntentId = null;
        }
    }

    /// The delete command confirmed the volume gone. Idempotent, never refuses. Returns true when it fulfilled the
    /// waiting delete.
    public bool MarkDeleted()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool fulfilled = DeleteIsWaiting;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentAttachedVmId = null;
        CurrentAttachedElsewhere = false;
        PendingIntentId = null;
        return fulfilled;
    }

    /// The create command gave up (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure): records why and ends the
    /// waiting create. Stale with no create waiting; the other give-ups are AbandonRequest.
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

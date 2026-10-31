using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// One snapshot of a VM: an instance snapshot on Incus named under the instance
/// (titan-ops features/vm/design.md#vm-snapshot). A child of Vm (titan-ops architecture/code-structure.md#child-entity):
/// the per-VM cap, "no VM delete while a snapshot command runs" and "the VM's delete takes its snapshots" are the
/// VM's invariants, so only the root reaches the methods below, and handlers go through Vm.AddSnapshot,
/// RevertSnapshot, RequestSnapshotDelete and MarkSnapshot*.
public class VmSnapshot : ChildEntity
{
    public Guid Id { get; private set; }
    public Guid VmId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    // Incus has no status word for an instance snapshot, only whether it is there
    // (titan-ops features/vm/design.md#vm-snapshot-is-existence). Same split as VolumeSnapshot.
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public Guid? PendingIntentId { get; private set; }
    public string? ExternalId { get; private set; }
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private VmSnapshot() { }

    /// Derived, never stored. A revert leaves DesiredExistence at Exists, so a waiting request on a snapshot Incus
    /// already had, and that is not a delete, is the revert.
    public VmSnapshotTransition? Transition
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
                return VmSnapshotTransition.Deleting;
            }
            if (CurrentExistence == CurrentExistence.NotCreated)
            {
                return VmSnapshotTransition.Creating;
            }
            return VmSnapshotTransition.Reverting;
        }
    }

    private bool CreateIsWaiting => Transition == VmSnapshotTransition.Creating;
    private bool RevertIsWaiting => Transition == VmSnapshotTransition.Reverting;
    private bool DeleteIsWaiting => Transition == VmSnapshotTransition.Deleting;

    /// Counted by occupancy toward MaxVmSnapshotsPerVm (titan-ops features/vm/design.md#vm-snapshot-counting): what Incus
    /// holds or is about to hold, a revert in flight included. A delete asked for stops counting at once; a failed
    /// create with nothing on Incus, and a snapshot Incus lost, do not count.
    public bool CountsTowardQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && PendingIntentId != null));

    /// A create or a revert still waiting touches the instance on Incus, so the VM's delete would race it
    /// (titan-ops features/vm/design.md#vm-snapshot-counting). A waiting delete does not block: the worker's snapshot
    /// delete no-ops once the VM is gone.
    public bool BlocksVmDelete => PendingIntentId != null && DesiredExistence == DesiredExistence.Exists;

    /// Called by Vm.AddSnapshot after the VM checked itself and the tenant's cap. Also the test seam: `internal`,
    /// reachable only from Titan.Tests (titan-ops architecture/code-structure.md#tenant-typed-edge).
    internal static VmSnapshot CreateUnchecked(Guid vmId, Guid tenantId, string name) => new()
    {
        Id = Guid.NewGuid(),
        VmId = vmId,
        TenantId = tenantId,
        Name = name,
        CurrentExistence = CurrentExistence.NotCreated,
        DesiredExistence = DesiredExistence.Exists,
        PendingIntentId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow
    };

    /// Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// The revert is the waiting request; existence stays. The VM's own rule is checked by Vm.RevertSnapshot before
    /// this is reached.
    internal string Revert()
    {
        bool restsOnIncus = PendingIntentId == null
                            && CurrentExistence == CurrentExistence.Exists
                            && DesiredExistence == DesiredExistence.Exists;
        if (!restsOnIncus)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to revert.");
        PendingIntentId = Guid.NewGuid();
        return RequireExternalId();
    }

    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    internal string RequestDelete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsTerminal)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        if (PendingIntentId != null || ExternalId is null)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must be on Incus, with nothing waiting, to delete.");
        DesiredExistence = DesiredExistence.Deleted;
        PendingIntentId = Guid.NewGuid();
        return ExternalId;
    }

    /// A revert or a delete command gave up (titan-ops architecture/desired-and-current.md#abandoned-request): the
    /// snapshot is still on Incus, so a delete returns DesiredExistence to Exists; a revert just ends. The reason is
    /// kept and the next request is a new one.
    internal void AbandonRequest(string? reason, string? detail)
    {
        bool revertOrDeleteIsWaiting = RevertIsWaiting || DeleteIsWaiting;
        if (!revertOrDeleteIsWaiting)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "No revert or delete is waiting on this snapshot.");
        DesiredExistence = DesiredExistence.Exists;
        PendingIntentId = null;
        RecordFailure(reason, detail);
    }

    /// Intent: a snapshot that never reached Incus has no command and no observation coming, so this writes the
    /// existence itself (titan-ops architecture/desired-and-current.md#delete-locally).
    internal void DeleteLocally()
    {
        bool neverReachedIncus = ExternalId is null && !CreateIsWaiting && !IsTerminal;
        if (!neverReachedIncus)
        {
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE",
                "Only a snapshot whose create failed before Incus had it can be deleted locally.");
        }
        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        PendingIntentId = null;
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public for the reconciler's read model.
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): converge, never throw.
    // Each returns true when the report fulfilled the waiting request, so the callback records its outcome once.

    /// The create command: Incus made the snapshot before answering, so this is the create done. A repeat keeps the
    /// first id.
    internal bool MarkCreated(string externalId)
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

    /// The reconciler found the snapshot on its VM. That proves it exists, not that a revert finished, so it ends
    /// only a waiting create.
    internal bool MarkObserved()
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

    /// The revert command reports the VM's disk is back at this snapshot.
    internal bool MarkReverted()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool revertWasWaiting = RevertIsWaiting;
        CurrentExistence = CurrentExistence.Exists;
        if (!revertWasWaiting)
        {
            return false;
        }
        PendingIntentId = null;
        return true;
    }

    /// The reconciler did not find the snapshot on its VM. Never Deleted: only Titan's own delete command confirms
    /// that (titan-ops architecture/desired-and-current.md#existence-axis). Ends a waiting create; a revert or delete keeps
    /// waiting for its command.
    internal void MarkNotFound()
    {
        if (IsTerminal)
        {
            return;
        }
        bool createWasWaiting = CreateIsWaiting;
        CurrentExistence = CurrentExistence.NotFound;
        if (createWasWaiting)
        {
            PendingIntentId = null;
        }
    }

    /// The delete command confirmed it gone, or the VM's delete took it along. Idempotent, never refuses; ends
    /// whatever request was waiting.
    internal bool MarkDeleted()
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
    /// waiting create. Stale with no create waiting; a revert's or delete's give-up is AbandonRequest.
    internal void MarkRequestFailed(string? reason, string? detail)
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

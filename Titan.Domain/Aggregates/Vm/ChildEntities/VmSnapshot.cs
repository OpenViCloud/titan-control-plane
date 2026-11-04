using System.Linq.Expressions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// One snapshot of a VM: an instance snapshot on Incus named under the instance
/// (titan-ops features/vm/design.md#vm-snapshot). A child of Vm (titan-ops architecture/code-structure.md#child-entity):
/// "no VM delete while a snapshot command runs" and "the VM's delete takes its snapshots" are the
/// VM's invariants, so only the root reaches the methods below, and handlers go through Vm.AddSnapshot,
/// RevertSnapshot, RequestSnapshotDelete, DeleteSnapshotLocally, EndSnapshotRequest and MarkSnapshot*.
public class VmSnapshot : ChildEntity
{
    /// A snapshot's name: 1 to 100 characters, not blank, any characters. The validator reads it. A property, not a const: a child
    /// has no public field.
    public static int NameMaxLength { get; } = 100;

    public Guid Id { get; private set; }
    public Guid VmId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    // Incus has no status word for an instance snapshot, only whether it is there
    // (titan-ops features/vm/design.md#vm-snapshot-is-existence). Mark* and a request's report write CurrentExistence;
    // a request, and a report that it failed, write DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current).
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public string? ExternalId { get; private set; }
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    // The one waiting request, as three columns (titan-ops architecture/resource-lifecycle.md#shape); all null or all
    // set, written by the factory and SetPending. Its own, independent of the VM's request.
    private Guid? PendingRequestId { get; set; }
    private VmSnapshotRequestKind? PendingRequestKind { get; set; }
    private DateTimeOffset? PendingRequestAskedAt { get; set; }

    /// The request this snapshot is waiting on, or null. Only the report carrying its id ends it
    /// (titan-ops architecture/resource-lifecycle.md#invariants).
    public PendingRequest<VmSnapshotRequestKind>? Pending =>
        PendingRequestId == null
            ? null
            : PendingRequest<VmSnapshotRequestKind>.Of(
                PendingRequestId.Value, PendingRequestKind!.Value, PendingRequestAskedAt!.Value);

    /// Billed while Incus holds it, a revert or a delete in flight included (titan-ops
    /// features/vm/design.md#vm-snapshot-counting). The repository lists with it in SQL.
    public static Expression<Func<VmSnapshot, bool>> IsBilled { get; } = s => s.CurrentExistence == CurrentExistence.Exists;

    private VmSnapshot() { }

    /// The move the waiting request makes, for display.
    public VmSnapshotTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case VmSnapshotRequestKind.Create:
                    return VmSnapshotTransition.Creating;
                case VmSnapshotRequestKind.Revert:
                    return VmSnapshotTransition.Reverting;
                case VmSnapshotRequestKind.Delete:
                    return VmSnapshotTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingRequestKind == VmSnapshotRequestKind.Create;
    private bool RevertIsWaiting => PendingRequestKind == VmSnapshotRequestKind.Revert;
    private bool DeleteIsWaiting => PendingRequestKind == VmSnapshotRequestKind.Delete;

    /// Counted by occupancy toward MaxVmSnapshotsPerVm (titan-ops features/vm/design.md#vm-snapshot-counting): what Incus
    /// holds or is about to hold, a revert in flight included. A delete asked for stops counting at once; a failed
    /// create with nothing on Incus, and a snapshot Incus lost, do not count.
    public bool CountsTowardQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && CreateIsWaiting));

    /// A create or a revert still waiting touches the instance on Incus, so the VM's delete would race it
    /// (titan-ops features/vm/design.md#vm-snapshot-counting). A waiting delete does not block: the worker's snapshot
    /// delete no-ops once the VM is gone.
    public bool BlocksVmDelete => CreateIsWaiting || RevertIsWaiting;

    /// Called by Vm.AddSnapshot after the VM checked itself.
    internal static VmSnapshot Create(Guid vmId, Guid tenantId, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > NameMaxLength)
        {
            throw new BusinessException("INVALID_SNAPSHOT_NAME", $"A snapshot name is 1 to {NameMaxLength} characters, not blank.");
        }
        var snapshot = new VmSnapshot
        {
            Id = Guid.NewGuid(),
            VmId = vmId,
            TenantId = tenantId,
            Name = name,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            CreatedAt = DateTime.UtcNow
        };
        snapshot.SetPending(NewRequest(VmSnapshotRequestKind.Create));
        return snapshot;
    }

    /// Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    /// Incus holds it and nothing waits: the only situation a snapshot can be reverted from.
    private bool RestsOnIncus =>
        PendingRequestId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    private static PendingRequest<VmSnapshotRequestKind> NewRequest(VmSnapshotRequestKind kind) =>
        PendingRequest<VmSnapshotRequestKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);

    private void SetPending(PendingRequest<VmSnapshotRequestKind>? request)
    {
        PendingRequestId = request?.Id;
        PendingRequestKind = request?.Kind;
        PendingRequestAskedAt = request?.AskedAt;
    }

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// The revert is the waiting request; existence stays. The VM's own rule is checked by Vm.RevertSnapshot before
    /// this is reached.
    internal RequestTicket Revert()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to revert.");
        string externalSnapshotId = RequireExternalId();
        PendingRequest<VmSnapshotRequestKind> request = NewRequest(VmSnapshotRequestKind.Revert);
        SetPending(request);
        return RequestTicket.Of(request.Id, externalSnapshotId);
    }

    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    internal RequestTicket RequestDelete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsTerminal)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        if (PendingRequestId != null || ExternalId is null)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must be on Incus, with nothing waiting, to delete.");
        PendingRequest<VmSnapshotRequestKind> request = NewRequest(VmSnapshotRequestKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(request);
        return RequestTicket.Of(request.Id, ExternalId);
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
        SetPending(null);
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public for the reconciler's read model.
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");

    // ---- The report of the snapshot's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends its request.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null. Done of the
    /// create records the id and existence, Done of the delete ends the snapshot, Done of the revert changes nothing
    /// else: the disk is back at the snapshot, which stays. Failed records the reason, keeps an id the worker found, and
    /// gives a delete back (titan-ops architecture/resource-lifecycle.md#kinds).
    internal PendingRequest<VmSnapshotRequestKind>? EndRequest(Guid requestId, VmSnapshotRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<VmSnapshotRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }

        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }

        if (end.Outcome == RequestOutcome.Done)
        {
            RequestFailureReason = null;
            RequestFailureDetail = null;
            RequestFailedAt = null;
            if (waiting!.Kind == VmSnapshotRequestKind.Delete)
            {
                CurrentExistence = CurrentExistence.Deleted;
            }
        }
        else
        {
            RequestFailureReason = end.Reason;
            RequestFailureDetail = end.Detail;
            RequestFailedAt = DateTimeOffset.UtcNow;
            if (waiting!.Kind == VmSnapshotRequestKind.Delete)
            {
                // Still on Incus: it counts against the quota again, and the next delete is a new request.
                DesiredExistence = DesiredExistence.Exists;
            }
        }

        SetPending(null);
        return waiting;
    }

    /// Incus deleted the instance, and this snapshot with it: the VM's delete Done ends the snapshot, whatever it waited
    /// on. Returns true when the snapshot's own delete was waiting, so the caller records its outcome once.
    internal bool EndWithItsVm()
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

    /// The reconciler found the snapshot on its VM. That proves it exists, not that a create or a revert finished.
    internal void MarkObserved()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler did not find the snapshot on its VM. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
    }
}

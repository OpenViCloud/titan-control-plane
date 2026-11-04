using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// One snapshot of a VM: an instance snapshot on Incus named under the instance
/// (titan-ops features/vm/design.md#vm-snapshot). A child of Vm (titan-ops architecture/code-structure.md#child-entity):
/// "no VM delete while a snapshot command runs" and "the VM's delete takes its snapshots" are the
/// VM's invariants, so only the root reaches the methods below, and handlers go through Vm.AddSnapshot,
/// RevertSnapshot, DeleteSnapshot, EndSnapshotOperation and MarkSnapshot*.
public class VmSnapshot : ProviderResource<VmSnapshotOperationKind>, IEntity
{
    /// A snapshot's name: 1 to 100 characters, not blank, any characters. The validator reads it. A property, not a const: a child
    /// has no public field.
    public static int NameMaxLength { get; } = 100;

    public Guid Id { get; private set; }
    public Guid VmId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    // Incus has no status word for an instance snapshot, only whether it is there
    // (titan-ops features/vm/design.md#vm-snapshot-is-existence). Mark* and an operation's report write CurrentExistence;
    // an operation, and a report that it failed, write DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current).
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public string? ExternalId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    /// Billed while Incus holds it, a revert or a delete in flight included (titan-ops
    /// features/vm/design.md#vm-snapshot-counting). VmSnapshotRepository.ListBillableAsync writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsBilled => CurrentExistence == CurrentExistence.Exists;

    /// Not yet Deleted (titan-ops architecture/domain-methods.md#plain-code): still listed under its VM, still
    /// observed. The repository's list and reconciler queries write the same test in SQL.
    public bool IsNotDeleted => CurrentExistence != CurrentExistence.Deleted;

    private VmSnapshot() { }

    /// The move the waiting operation makes, for display.
    public VmSnapshotTransition? Transition
    {
        get
        {
            switch (PendingOperationKind)
            {
                case VmSnapshotOperationKind.Create:
                    return VmSnapshotTransition.Creating;
                case VmSnapshotOperationKind.Revert:
                    return VmSnapshotTransition.Reverting;
                case VmSnapshotOperationKind.Delete:
                    return VmSnapshotTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingOperationKind == VmSnapshotOperationKind.Create;
    private bool RevertIsWaiting => PendingOperationKind == VmSnapshotOperationKind.Revert;
    private bool DeleteIsWaiting => PendingOperationKind == VmSnapshotOperationKind.Delete;
    /// A delete was asked and still waits, or the snapshot is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsTerminal;

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
        snapshot.SetPending(NewOperation(VmSnapshotOperationKind.Create));
        return snapshot;
    }

    /// Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    /// Incus holds it and nothing waits: the only situation a snapshot can be reverted from.
    private bool RestsOnIncus =>
        PendingOperationId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// The revert is the waiting operation; existence stays. The VM's own rule is checked by Vm.RevertSnapshot before
    /// this is reached.
    internal OperationTicket Revert()
    {
        if (!RestsOnIncus)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to revert.");
        string externalSnapshotId = RequireExternalId();
        PendingOperation<VmSnapshotOperationKind> operation = NewOperation(VmSnapshotOperationKind.Revert);
        SetPending(operation);
        return OperationTicket.Of(operation.Id, externalSnapshotId);
    }

    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    /// A snapshot that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    internal OperationTicket? Delete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsTerminal)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
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
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must be on Incus, with nothing waiting, to delete.");
        PendingOperation<VmSnapshotOperationKind> operation = NewOperation(VmSnapshotOperationKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(operation);
        return OperationTicket.Of(operation.Id, ExternalId);
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public for the reconciler's read model.
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");

    // ---- The report of the snapshot's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends its operation.

    /// Ends the waiting operation when the report names it; any other report is ignored and returns null. Done of the
    /// create records the id and existence, Done of the delete ends the snapshot, Done of the revert changes nothing
    /// else: the disk is back at the snapshot, which stays. Failed records the reason, keeps an id the worker found, and
    /// gives a delete back (titan-ops architecture/resource-lifecycle.md#kinds).
    internal PendingOperation<VmSnapshotOperationKind>? EndOperation(Guid operationId, VmSnapshotOperationEnd end)
    {
        if (IsTerminal) return null;
        PendingOperation<VmSnapshotOperationKind>? waiting = Pending;
        bool reportIsForThePendingOperation = waiting != null && waiting.Id == operationId;
        if (!reportIsForThePendingOperation)
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

        if (end.Outcome == OperationOutcome.Done)
        {
            ClearFailure();
            if (waiting!.Kind == VmSnapshotOperationKind.Delete)
            {
                CurrentExistence = CurrentExistence.Deleted;
            }
        }
        else
        {
            RecordFailure(end.Reason, end.Detail);
            if (waiting!.Kind == VmSnapshotOperationKind.Delete)
            {
                // Still on Incus: it counts against the quota again, and the next delete is a new operation.
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
    // ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

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

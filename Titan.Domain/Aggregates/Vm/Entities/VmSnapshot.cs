using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// One snapshot of a VM: an instance snapshot on Incus named under the instance
/// (titan-ops features/vm/design.md#vm-snapshot). A child of Vm (titan-ops architecture/code-structure.md#child-entity):
/// "no VM delete while a snapshot command runs" and "the VM's delete takes its snapshots" are the
/// VM's invariants, so only the root reaches the methods below, and handlers go through Vm.AddSnapshot,
/// RevertSnapshot, DeleteSnapshot, FinishSnapshotOperation, FailSnapshotOperation and MarkSnapshot*.
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

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// Until then the snapshot is still listed under its VM and still observed; the repository's list and reconciler
    /// queries write !IsDeleted in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => CurrentExistence == CurrentExistence.Deleted;

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
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsDeleted;

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

    /// Called by Vm.AddSnapshot after the VM checked itself, with the ids the handler chose: the snapshot's and its
    /// create's.
    internal static VmSnapshot Create(Guid id, Guid vmId, Guid tenantId, string name, Guid operationId)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "A snapshot needs an id.");
        }
        var snapshot = new VmSnapshot
        {
            Id = id,
            VmId = vmId,
            TenantId = tenantId,
            Name = name,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            CreatedAt = DateTime.UtcNow
        };
        snapshot.SetPending(PendingOperation<VmSnapshotOperationKind>.Of(operationId, VmSnapshotOperationKind.Create, DateTimeOffset.UtcNow));
        return snapshot;
    }

    /// Incus holds it and nothing waits: the only situation a snapshot can be reverted from.
    private bool RestsOnIncus =>
        PendingOperationId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// The revert is the waiting operation, with the id the handler chose; existence stays. The VM's own rule is
    /// checked by Vm.RevertSnapshot before this is reached.
    internal void Revert(Guid operationId)
    {
        if (!RestsOnIncus)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must exist on Incus, with nothing waiting, to revert.");
        SetPending(PendingOperation<VmSnapshotOperationKind>.Of(operationId, VmSnapshotOperationKind.Revert, DateTimeOffset.UtcNow));
    }

    /// Follows Incus: a snapshot with a provider id is deleted through Incus, even one Incus no longer has; the
    /// existence waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm).
    /// Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    /// A snapshot that never reached Incus is deleted here, at once: IsDeleted tells the handler there is no command
    /// to send (titan-ops architecture/desired-and-current.md#delete-locally).
    internal void Delete(Guid operationId)
    {
        if (DeleteIsWaiting)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "A delete is already in progress for this snapshot.");
        if (IsDeleted)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "The snapshot is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && PendingOperationId == null;
        if (neverReachedIncus)
        {
            DesiredExistence = DesiredExistence.Deleted;
            CurrentExistence = CurrentExistence.Deleted;
            SetPending(null);
            return;
        }
        if (PendingOperationId != null || ExternalId is null)
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot must be on Incus, with nothing waiting, to delete.");
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(PendingOperation<VmSnapshotOperationKind>.Of(operationId, VmSnapshotOperationKind.Delete, DateTimeOffset.UtcNow));
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public: a revert or delete
    /// command names the snapshot by it, and the reconciler's read model too.
    public string GetExternalId()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("VM_SNAPSHOT_INVALID_STATE", "Snapshot has no provider resource yet.");
        }
        return ExternalId;
    }

    // ---- The report of the snapshot's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends its operation.

    /// The command ended Done. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null. The create records the id and existence, the delete ends the snapshot, the revert changes nothing
    /// else: the disk is back at the snapshot, which stays (titan-ops architecture/resource-lifecycle.md#kinds).
    internal VmSnapshotOperationKind? FinishOperation(Guid operationId, string? externalId)
    {
        if (IsDeleted) return null;
        PendingOperation<VmSnapshotOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        EnsureProviderIdOnDone(ExternalId, externalId);
        RecordWhatTheWorkerSaw(externalId);

        ClearFailure();
        if (waiting.Kind == VmSnapshotOperationKind.Delete)
        {
            CurrentExistence = CurrentExistence.Deleted;
        }

        SetPending(null);
        return waiting.Kind;
    }

    /// The command gave up. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null. Records the reason, keeps an id the worker found, and gives a delete back
    /// (titan-ops architecture/resource-lifecycle.md#kinds).
    internal VmSnapshotOperationKind? FailOperation(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (IsDeleted) return null;
        PendingOperation<VmSnapshotOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RecordWhatTheWorkerSaw(externalId);

        RecordFailure(reason, detail);
        if (waiting.Kind == VmSnapshotOperationKind.Delete)
        {
            // Still on Incus: it counts against the quota again, and the next delete is a new operation.
            DesiredExistence = DesiredExistence.Exists;
        }

        SetPending(null);
        return waiting.Kind;
    }

    private PendingOperation<VmSnapshotOperationKind>? WaitingOperationNamed(Guid operationId)
    {
        PendingOperation<VmSnapshotOperationKind>? waiting = Pending;
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

    /// Incus deleted the instance, and this snapshot with it: the VM's delete Done ends the snapshot, whatever it waited
    /// on. A snapshot already Deleted stays as it is.
    internal void EndWithItsVm()
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

    /// The reconciler found the snapshot on its VM. That proves it exists, not that a create or a revert finished.
    internal void MarkObserved()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler did not find the snapshot on its VM. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.NotFound;
    }
}

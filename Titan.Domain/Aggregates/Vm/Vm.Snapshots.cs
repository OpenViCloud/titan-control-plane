using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    // ---- Snapshots: the VM counts its snapshots, is Snapshotting while a create or revert runs, and routes each call
    // to the snapshot it names (titan-ops architecture/code-structure.md#child-entity). Logic stays on VmSnapshot; this
    // is the way in.

    /// The precondition of AddSnapshot and RevertSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!IncusCanSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to snapshot or revert.");
    }

    /// Intent: a new snapshot of this VM, with the ids the handler chose: the snapshot's and its create's. The VM must
    /// accept a snapshot (EnsureSnapshottable) and is Snapshotting until the create reports. The create is the
    /// snapshot's own Pending, not the VM's; its command snapshots the instance ExternalId names.
    public void AddSnapshot(Guid snapshotId, Guid operationId, string name)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        EnsureSnapshottable();
        EnsureOnTheProvider();
        VmSnapshot snapshot = VmSnapshot.Create(snapshotId, Id, TenantId, name, operationId);
        _snapshots.Add(snapshot);
        Status = VmStatus.Snapshotting;
    }

    /// A snapshot this VM does not have answers like a missing one, the same 404 a snapshot of another VM gets
    /// (titan-ops features/vm/design.md#vm-routes).
    private VmSnapshot GetSnapshot(Guid snapshotId)
    {
        foreach (VmSnapshot snapshot in _snapshots)
        {
            if (snapshot.Id == snapshotId)
            {
                return snapshot;
            }
        }
        throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
    }

    /// Intent on one snapshot, under the id the handler chose: the VM must accept a revert (its own rule first), then
    /// the snapshot must rest on Incus, and the VM is Snapshotting until the revert reports. Titan does not stop or
    /// start the VM for it.
    public void RevertSnapshot(Guid snapshotId, Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        VmSnapshot snapshot = GetSnapshot(snapshotId);
        EnsureSnapshottable();
        snapshot.Revert(operationId);
        Status = VmStatus.Snapshotting;
    }

    /// Intent on one snapshot, under the id the handler chose: its delete goes through Incus, or, when the snapshot
    /// never reached Incus, it is Deleted here at once.
    public void DeleteSnapshot(Guid snapshotId, Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        GetSnapshot(snapshotId).Delete(operationId);
    }

    /// The report that the snapshot's own command ended Done (titan-ops architecture/resource-lifecycle.md#shape); null
    /// when it names another operation than the one the snapshot waits on. A create or revert that ends takes the VM
    /// out of Snapshotting, but only for a VM that still is Snapshotting: a VM at rest has nothing to leave, so its
    /// status does not change.
    public VmSnapshotOperationKind? FinishSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId)
    {
        VmSnapshotOperationKind? ended = GetSnapshot(snapshotId).FinishOperation(operationId, externalId);
        if (Status == VmStatus.Snapshotting && IsACreateOrARevert(ended))
        {
            Status = StatusOfTheInstance();
        }
        return ended;
    }

    /// The report that the snapshot's own command gave up; null when it names another operation than the one the
    /// snapshot waits on. A create or revert that ends takes the VM out of Snapshotting, only for a VM that still is.
    public VmSnapshotOperationKind? FailSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId,
        string? reason, string? detail)
    {
        VmSnapshotOperationKind? ended = GetSnapshot(snapshotId).FailOperation(operationId, externalId, reason, detail);
        if (Status == VmStatus.Snapshotting && IsACreateOrARevert(ended))
        {
            Status = StatusOfTheInstance();
        }
        return ended;
    }

    /// Only a create or a revert made the VM Snapshotting: the end of a snapshot's delete must not clear a Snapshotting
    /// that another snapshot's create or revert set.
    private static bool IsACreateOrARevert(VmSnapshotOperationKind? ended) =>
        ended == VmSnapshotOperationKind.Create || ended == VmSnapshotOperationKind.Revert;

    /// The status of a VM no operation waits on: the Incus word it holds, Unknown without one, as a Start or Stop that
    /// gave up goes back to it.
    private VmStatus StatusOfTheInstance()
    {
        if (ObservedState == null)
        {
            return VmStatus.Unknown;
        }
        return ObservedState.Value;
    }

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkSnapshotObserved(Guid snapshotId) => GetSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => GetSnapshot(snapshotId).MarkNotFound();
}

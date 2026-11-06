using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class Vm
{
    // ---- Snapshots: the VM counts its snapshots, keeps the "no delete under a snapshot command" rule, and routes each
    // call to the snapshot it names (titan-ops architecture/code-structure.md#child-entity). Logic stays on VmSnapshot;
    // this is the way in.

    /// The precondition of AddSnapshot and RevertSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to snapshot or revert.");
    }

    /// Intent: a new snapshot of this VM, with the ids the handler chose: the snapshot's and its create's. The VM must
    /// accept a snapshot (EnsureSnapshottable). The create is the snapshot's own Pending; its command snapshots the
    /// instance ExternalId names.
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
    /// the snapshot must rest on Incus. Titan does not stop or start the VM for it.
    public void RevertSnapshot(Guid snapshotId, Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        VmSnapshot snapshot = GetSnapshot(snapshotId);
        EnsureSnapshottable();
        snapshot.Revert(operationId);
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
    /// when it names another operation than the one the snapshot waits on.
    public VmSnapshotOperationKind? FinishSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId) =>
        GetSnapshot(snapshotId).FinishOperation(operationId, externalId);

    /// The report that the snapshot's own command gave up; null when it names another operation than the one the
    /// snapshot waits on.
    public VmSnapshotOperationKind? FailSnapshotOperation(Guid snapshotId, Guid operationId, string? externalId,
        string? reason, string? detail) =>
        GetSnapshot(snapshotId).FailOperation(operationId, externalId, reason, detail);

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkSnapshotObserved(Guid snapshotId) => GetSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => GetSnapshot(snapshotId).MarkNotFound();
}

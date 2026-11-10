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
    /// snapshot's own operation, not the VM's; its command snapshots the instance whose name this hands back
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    public string AddSnapshot(Guid snapshotId, Guid operationId, string name)
    {
        if (operationId == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        EnsureSnapshottable();
        EnsureOnTheProvider();
        VmSnapshot snapshot = VmSnapshot.Create(snapshotId, Id, TenantId, name, operationId);
        _snapshots.Add(snapshot);
        Operation = Operation.Asked(null, null, InstanceStatus);
        Status = VmStatus.Snapshotting;
        return ExternalId!;
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
        Operation = Operation.Asked(null, null, InstanceStatus);
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

    /// The reports of the snapshot's own commands (titan-ops features/vm/design.md#vm-snapshot-reports); each is false when
    /// it names another operation than the one the snapshot waits on, which its status says. A create or revert that ends
    /// takes the VM out of Snapshotting, but only for a VM that still is Snapshotting: a VM at rest has nothing to leave,
    /// so its status does not change. A delete's end never touches the VM: only a create or a revert made it Snapshotting.
    public bool SnapshotCreateSucceeded(Guid snapshotId, Guid operationId, string? externalId)
    {
        bool ended = GetSnapshot(snapshotId).CreateSucceeded(operationId, externalId);
        if (ended)
        {
            LeaveSnapshotting();
        }
        return ended;
    }

    public bool SnapshotCreateFailed(Guid snapshotId, Guid operationId, string? externalId, string? reason, string? detail)
    {
        bool ended = GetSnapshot(snapshotId).CreateFailed(operationId, externalId, reason, detail);
        if (ended)
        {
            LeaveSnapshotting();
        }
        return ended;
    }

    public bool SnapshotRevertSucceeded(Guid snapshotId, Guid operationId)
    {
        bool ended = GetSnapshot(snapshotId).RevertSucceeded(operationId);
        if (ended)
        {
            LeaveSnapshotting();
        }
        return ended;
    }

    public bool SnapshotRevertFailed(Guid snapshotId, Guid operationId, string? reason, string? detail)
    {
        bool ended = GetSnapshot(snapshotId).RevertFailed(operationId, reason, detail);
        if (ended)
        {
            LeaveSnapshotting();
        }
        return ended;
    }

    public bool SnapshotDeleteSucceeded(Guid snapshotId, Guid operationId) =>
        GetSnapshot(snapshotId).DeleteSucceeded(operationId);

    public bool SnapshotDeleteFailed(Guid snapshotId, Guid operationId, string? reason, string? detail) =>
        GetSnapshot(snapshotId).DeleteFailed(operationId, reason, detail);

    /// A snapshot create or revert ended: a VM still Snapshotting goes back to the Incus status it holds, Unknown without
    /// one, as a Start or Stop that gave up goes back to it, and keeps no status seen afterwards: it is at rest.
    private void LeaveSnapshotting()
    {
        if (Status != VmStatus.Snapshotting)
        {
            return;
        }
        VmStatus resting = InstanceStatus ?? VmStatus.Unknown;
        Operation = Operation.ReturnedToRest();
        Status = resting;
    }

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges): what Incus holds.

    public void MarkSnapshotObserved(Guid snapshotId) => GetSnapshot(snapshotId).MarkObserved();

    public void MarkSnapshotNotFound(Guid snapshotId) => GetSnapshot(snapshotId).MarkNotFound();
}

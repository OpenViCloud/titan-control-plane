using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Vm;

/// One snapshot of a VM: an instance snapshot on Incus named under the instance
/// (titan-ops features/vm/design.md#vm-snapshot). A child of Vm (titan-ops architecture/code-structure.md#child-entity):
/// "no VM delete while a snapshot command runs" and "the VM's delete takes its snapshots" are the
/// VM's invariants, so only the root reaches the methods below, and handlers go through Vm.AddSnapshot,
/// RevertSnapshot, DeleteSnapshot, SnapshotCreateSucceeded, SnapshotCreateFailed, SnapshotRevertSucceeded,
/// SnapshotRevertFailed, SnapshotDeleteSucceeded, SnapshotDeleteFailed and MarkSnapshot*.
public partial class VmSnapshot : IEntity
{
    /// A snapshot's name: 1 to 100 characters, not blank, any characters. The validator reads it. A property, not a const: a child
    /// has no public field.
    public static int NameMaxLength { get; } = 100;

    public Guid Id { get; private set; }
    public Guid VmId { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    /// The one status the API shows (titan-ops features/vm/design.md#vm-snapshot). Only an operation, a report ending it, the
    /// VM's delete taking it along, or an observation at rest writes it, each through the moves of VmSnapshot.Creation.cs, VmSnapshot.Revert.cs,
    /// VmSnapshot.Deletion.cs and VmSnapshot.Observation.cs.
    public VmSnapshotStatus Status { get; private set; }
    /// The snapshot's own operation, independent of its VM's, the last one that failed, and whether Incus was last seen
    /// holding the snapshot while one waits.
    public VmSnapshotOperation Operation { get; private set; } = VmSnapshotOperation.None;
    public string? ExternalId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private VmSnapshot() { }

    /// Whether Incus holds the snapshot as far as Titan last knew: Available, or null. While an operation waits it is what
    /// the operation last saw; at rest it is Available when the status is. Worked out, never stored, and not on /v1.
    public VmSnapshotStatus? InstanceStatus
    {
        get
        {
            if (IsTransitionStatus)
            {
                return Operation.LastSeenStatus;
            }
            if (Status == VmSnapshotStatus.Available)
            {
                return VmSnapshotStatus.Available;
            }
            return null;
        }
    }

    /// An operation waits on its command: only the report carrying its id moves the status on.
    private bool IsTransitionStatus =>
        Status == VmSnapshotStatus.Creating
        || Status == VmSnapshotStatus.Reverting
        || Status == VmSnapshotStatus.Deleting;

    private bool IncusHoldsIt => InstanceStatus == VmSnapshotStatus.Available;

    /// Billed while Incus holds it, a revert or a delete in flight included (titan-ops
    /// features/vm/design.md#vm-snapshot-counting). VmSnapshotRepository.ListBillableAsync writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsBilled => IncusHoldsIt;

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// Until then the snapshot is still listed under its VM and still observed; the repository's list and reconciler
    /// queries write !IsDeleted in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => Status == VmSnapshotStatus.Deleted;

    /// A delete was asked and still waits, or the snapshot is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => Status == VmSnapshotStatus.Deleting || IsDeleted;

    /// A revert was asked and still waits: a later revert is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsReverting => Status == VmSnapshotStatus.Reverting;

    /// Counted by occupancy toward MaxVmSnapshotsPerVm (titan-ops features/vm/design.md#vm-snapshot-counting): what Incus
    /// holds or is about to hold, a revert in flight included. A delete asked for stops counting at once; a failed
    /// create with nothing on Incus, and a snapshot Incus lost, do not count.
    public bool CountsTowardQuota =>
        Status == VmSnapshotStatus.Creating
        || Status == VmSnapshotStatus.Available
        || (Status == VmSnapshotStatus.Reverting && IncusHoldsIt);

    // ---- The report of the snapshot's own command (titan-ops features/vm/design.md#vm-snapshot-reports): the one thing
    // that ends its operation; CreateSucceeded and CreateFailed are in VmSnapshot.Creation.cs, RevertSucceeded and
    // RevertFailed in VmSnapshot.Revert.cs, DeleteSucceeded and DeleteFailed in VmSnapshot.Deletion.cs.

    /// The report names the operation the snapshot waits on, which its status says.
    private bool IsWaitingOn(Guid operationId, VmSnapshotStatus waiting) =>
        Status == waiting && Operation.PendingId == operationId;

    /// The status a revert or a delete that ended goes back to: whether Incus was last seen holding the snapshot.
    private VmSnapshotStatus RestingStatus => InstanceStatus ?? VmSnapshotStatus.NotFound;

    /// A report brings the provider id with it, once.
    private void RecordTheId(string? externalId)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
        }
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
}

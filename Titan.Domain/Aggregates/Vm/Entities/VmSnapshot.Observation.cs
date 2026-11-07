namespace Titan.Domain.Aggregates.Vm;

public partial class VmSnapshot
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): the status at rest, or only
    // what is seen while an operation waits; never throws, ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler found the snapshot on its VM. That proves it exists, not that a create or a revert finished.
    internal void MarkObserved()
    {
        if (IsDeleted || Status == VmSnapshotStatus.Failed) return;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(VmSnapshotStatus.Available);
        }
        else
        {
            Status = VmSnapshotStatus.Available;
        }
    }

    /// The reconciler did not find the snapshot on its VM. Never Deleted: only a delete writes that, never an observation
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsDeleted || Status == VmSnapshotStatus.Failed) return;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(null);
        }
        else
        {
            Status = VmSnapshotStatus.NotFound;
        }
    }
}

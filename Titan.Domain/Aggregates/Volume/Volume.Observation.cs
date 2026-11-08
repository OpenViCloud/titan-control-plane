namespace Titan.Domain.Aggregates.Volume;

public partial class Volume
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): the status at rest, or only
    // what is seen while an operation waits; never throws, ends no operation (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the volume on Incus: the Titan VM using it, and whether an instance outside Titan does. That
    /// proves it exists, not that an operation finished.
    public void MarkObserved(Guid? attachedVmId, bool attachedElsewhere)
    {
        if (IsDeleted || Status == VolumeStatus.Failed) return;
        CurrentAttachedVmId = attachedVmId;
        CurrentAttachedElsewhere = attachedElsewhere;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(VolumeStatus.Available);
        }
        else
        {
            Status = VolumeStatus.Available;
        }
    }

    /// The reconciler did not find the volume. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsDeleted || Status == VolumeStatus.Failed) return;
        CurrentAttachedVmId = null;
        CurrentAttachedElsewhere = false;
        if (IsTransitionStatus)
        {
            Operation = Operation.Saw(null);
        }
        else
        {
            Status = VolumeStatus.NotFound;
        }
    }
}

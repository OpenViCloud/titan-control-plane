namespace Titan.Domain.Aggregates.Vm;

/// The move the snapshot's waiting request makes, for display; derived from its kind, never stored.
public enum VmSnapshotTransition
{
    Creating,
    Reverting,
    Deleting
}

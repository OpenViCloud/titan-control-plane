namespace Titan.Domain.Aggregates.Volume;

/// The kinds of operation a volume snapshot can wait on, independent of its volume's operation
/// (titan-ops architecture/resource-lifecycle.md#invariants, I-1).
public enum VolumeSnapshotOperationKind
{
    Create,
    Delete
}

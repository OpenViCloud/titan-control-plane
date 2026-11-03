namespace Titan.Domain.Aggregates.Volume;

/// The kinds of request a volume snapshot can wait on, independent of its volume's request
/// (titan-ops architecture/resource-lifecycle.md#invariants, I-1).
public enum VolumeSnapshotRequestKind
{
    Create,
    Delete
}

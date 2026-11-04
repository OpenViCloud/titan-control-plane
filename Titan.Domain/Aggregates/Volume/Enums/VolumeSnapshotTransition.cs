namespace Titan.Domain.Aggregates.Volume;

/// The move the snapshot's waiting operation makes, for display; derived from its kind, never stored.
public enum VolumeSnapshotTransition
{
    Creating,
    Deleting
}

namespace Titan.Domain.Aggregates.Volume;

/// The move the waiting request makes, for display; derived from its kind, never stored.
public enum VolumeTransition
{
    Creating,
    Attaching,
    Detaching,
    Deleting
}

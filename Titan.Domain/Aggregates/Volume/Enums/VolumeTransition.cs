namespace Titan.Domain.Aggregates.Volume;

/// The request still waiting for Incus, derived from the desired pair + PendingIntentId — never stored.
public enum VolumeTransition
{
    Creating,
    Attaching,
    Detaching,
    Deleting
}

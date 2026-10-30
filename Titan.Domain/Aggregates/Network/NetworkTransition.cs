namespace Titan.Domain.Aggregates.Network;

/// The request a network is waiting on, for display. Derived, never stored. No transition while DesiredState
/// differs from CurrentState means drift — for example the network vanished from the provider outside Titan.
public enum NetworkTransition
{
    Creating,
    Deleting
}

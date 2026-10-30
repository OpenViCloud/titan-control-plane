namespace Titan.Domain.Aggregates.Vm;

/// The request a VM is waiting on, named as the move it makes. Derived from DesiredState and the waiting
/// request, never stored. No transition while DesiredState differs from CurrentState means drift — the VM
/// changed without anyone asking (a guest-OS shutdown), not a request in flight.
public enum VmTransition
{
    Creating,
    Starting,
    Stopping,
    Deleting
}

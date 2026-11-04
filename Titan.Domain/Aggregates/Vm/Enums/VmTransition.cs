namespace Titan.Domain.Aggregates.Vm;

/// The operation a VM is waiting on, named as the move it makes. Derived from DesiredState and the waiting
/// operation, never stored. No transition while DesiredState differs from CurrentState means drift — the VM
/// changed without anyone asking (a guest-OS shutdown), not an operation in flight.
public enum VmTransition
{
    Creating,
    Starting,
    Stopping,
    Deleting
}

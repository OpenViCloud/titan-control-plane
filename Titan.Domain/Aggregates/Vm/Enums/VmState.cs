namespace Titan.Domain.Aggregates.Vm;

/// The Incus instance status, word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim):
/// exactly the five a QEMU instance reports (incus driver_qemu.go statusCode). Only an observation writes it;
/// Vm.CurrentState is null while Incus has no instance. Whether the instance exists is Vm.CurrentExistence.
public enum VmState
{
    Running,
    Ready,
    Stopped,
    Frozen,
    Error
}

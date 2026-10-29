namespace Titan.Domain.Aggregates.Compute.Vm;

/// The instance status a person asked for, in Incus words (titan-ops architecture/desired-and-current.md#desired-and-current).
/// Only a request writes it. A delete is Vm.DesiredExistence, not a status.
public enum VmDesiredState
{
    Running,
    Stopped
}

namespace Titan.Domain.Aggregates.Compute.Vm;

/// Derived, not stored (titan-ops features/vm/design.md#pending-intent). Observation methods return it so the
/// caller records each request's outcome once.
public enum VmIntent
{
    Create,
    Stop,
    Start,
    Delete
}

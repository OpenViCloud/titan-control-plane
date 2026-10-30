namespace Titan.Domain.Aggregates.Vm;

/// The request still waiting for the provider, derived — never stored.
public enum VmSnapshotTransition
{
    Creating,
    Reverting,
    Deleting
}

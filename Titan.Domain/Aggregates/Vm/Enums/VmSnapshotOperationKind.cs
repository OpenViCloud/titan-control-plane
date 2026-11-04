namespace Titan.Domain.Aggregates.Vm;

/// The kinds of operation a VM snapshot can wait on, one at a time and independent of its VM's operation
/// (titan-ops architecture/resource-lifecycle.md#kinds). A revert leaves existence as it is, so only the stored kind tells
/// it from a create or a delete.
public enum VmSnapshotOperationKind
{
    Create,
    Revert,
    Delete
}

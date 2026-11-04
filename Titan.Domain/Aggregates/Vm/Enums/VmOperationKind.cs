namespace Titan.Domain.Aggregates.Vm;

/// The kinds of operation a VM can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds). Stored
/// with the operation's id; the id alone says which operation a report ends, the kind says what ending it does.
public enum VmOperationKind
{
    Create,
    Start,
    Stop,
    Delete
}

namespace Titan.Domain.Aggregates.Vm;

/// The kinds of request a VM can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds). Stored
/// with the request's id; the id alone says which request a report ends, the kind says what ending it does.
public enum VmRequestKind
{
    Create,
    Start,
    Stop,
    Delete
}

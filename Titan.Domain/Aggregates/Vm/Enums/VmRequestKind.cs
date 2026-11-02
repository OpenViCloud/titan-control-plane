namespace Titan.Domain.Aggregates.Vm;

/// The kinds of request a VM can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds). Stored
/// with the request's id, so a report is matched to the request it ends, not to whatever is waiting now.
public enum VmRequestKind
{
    Create,
    Start,
    Stop,
    Delete
}

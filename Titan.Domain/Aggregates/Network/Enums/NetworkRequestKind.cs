namespace Titan.Domain.Aggregates.Network;

/// The kinds of request a network can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds). Stored
/// with the request's id; the id says which request a report ends, the kind says what ending it does.
public enum NetworkRequestKind
{
    Create,
    Delete
}

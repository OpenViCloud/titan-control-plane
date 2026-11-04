namespace Titan.Domain.Aggregates.Network;

/// The kinds of operation a network can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds). Stored
/// with the operation's id; the id says which operation a report ends, the kind says what ending it does.
public enum NetworkOperationKind
{
    Create,
    Delete
}

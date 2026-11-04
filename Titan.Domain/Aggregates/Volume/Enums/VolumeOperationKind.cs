namespace Titan.Domain.Aggregates.Volume;

/// The kinds of operation a volume can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds). A
/// restore is the new volume's Create. Stored with the operation's id; the id says which operation a report ends, the kind
/// says what ending it does.
public enum VolumeOperationKind
{
    Create,
    Attach,
    Detach,
    Delete
}

namespace Titan.Domain.Aggregates.Volume;

/// The kinds of request a volume can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds). A
/// restore is the new volume's Create. Stored with the request's id; the id says which request a report ends, the kind
/// says what ending it does.
public enum VolumeRequestKind
{
    Create,
    Attach,
    Detach,
    Delete
}

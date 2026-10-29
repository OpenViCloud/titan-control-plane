namespace Titan.Domain.Values;

/// Whether the provider holds the object, as last observed — the half of a resource's state the provider has no
/// status word for (titan-ops architecture/desired-and-current.md#existence-axis). Only an observation writes it, like
/// CurrentState. NotCreated: asked for, not on the provider yet. NotFound: Titan still holds it, the provider
/// does not. Deleted: gone after Titan's own delete; the row is kept.
public enum CurrentExistence
{
    NotCreated,
    Exists,
    NotFound,
    Deleted
}

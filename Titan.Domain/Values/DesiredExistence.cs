namespace Titan.Domain.Values;

/// Whether a person wants the object to exist (titan-ops architecture/desired-and-current.md#existence-axis). Only a
/// request writes it; Deleted is a delete asked for and not yet confirmed by the provider.
public enum DesiredExistence
{
    Exists,
    Deleted
}

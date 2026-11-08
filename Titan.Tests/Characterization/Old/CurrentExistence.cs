// Frozen copy of origin/main e36035b, the shape before the one-status change; the harness compares the live
// aggregate against it. Only the namespace and using lines differ from the original; do not edit.
namespace Titan.Tests.Characterization.Old;

/// Whether the provider holds the object, as last observed — the half of a resource's state the provider has no
/// status word for (titan-ops architecture/desired-and-current.md#existence-axis). Written from what the worker reports,
/// never by an operation, like CurrentState; the delete of a resource that never reached the provider apart
/// (titan-ops architecture/desired-and-current.md#delete-locally). NotCreated: asked for, not on the provider yet.
/// NotFound: Titan still holds it, the provider does not. Deleted: gone after Titan's own delete; the row is kept.
public enum CurrentExistence
{
    NotCreated,
    Exists,
    NotFound,
    Deleted
}

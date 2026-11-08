// Frozen copy of origin/main e36035b, the shape before the one-status change; the harness compares the live
// aggregate against it. Only the namespace and using lines differ from the original; do not edit.
namespace Titan.Tests.Characterization.Old;

/// Whether a person wants the object to exist (titan-ops architecture/desired-and-current.md#existence-axis). Only a
/// operation writes it; Deleted is a delete asked for and not yet confirmed by the provider.
public enum DesiredExistence
{
    Exists,
    Deleted
}

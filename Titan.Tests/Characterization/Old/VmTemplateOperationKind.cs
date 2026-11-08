// Frozen copy of origin/main e36035b, the shape before the one-status change; the harness compares the live
// aggregate against it. Only the namespace and using lines differ from the original; do not edit.
namespace Titan.Tests.Characterization.Old;

/// The one kind of operation a template can wait on in a cluster (titan-ops architecture/resource-lifecycle.md#kinds): sync is
/// add-only, so there is nothing else to ask for. Stored with the operation's id, as every resource's kind is.
public enum VmTemplateOperationKind
{
    Sync
}

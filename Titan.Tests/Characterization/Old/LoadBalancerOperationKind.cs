// Frozen copy of origin/main e36035b, the shape before the one-status change; the harness compares the live
// aggregate against it. Only the namespace and using lines differ from the original; do not edit.
namespace Titan.Tests.Characterization.Old;

/// The kinds of operation a load balancer can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds).
/// Stored with the operation's id; the id says which operation a report ends, the kind says what ending it does. Its
/// listener and backend rows carry no operation of their own.
public enum LoadBalancerOperationKind
{
    Create,
    Update,
    Delete
}

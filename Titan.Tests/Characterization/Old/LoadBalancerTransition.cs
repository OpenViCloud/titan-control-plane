// Frozen copy of origin/main e36035b, the shape before the one-status change; the harness compares the live
// aggregate against it. Only the namespace and using lines differ from the original; do not edit.
namespace Titan.Tests.Characterization.Old;

/// The move the waiting operation makes, for display; derived from its kind, never stored
/// (titan-ops features/load-balancer/design.md#lb-lifecycle).
public enum LoadBalancerTransition
{
    Creating,
    Updating,
    Deleting
}

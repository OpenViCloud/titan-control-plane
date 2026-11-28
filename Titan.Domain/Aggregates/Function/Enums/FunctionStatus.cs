namespace Titan.Domain.Aggregates.Function;

/// The one status of a function, the only one the API shows (titan-ops features/ovc-function/design.md#function-lifecycle).
/// Deploying and Deleting say an operation waits on its command; Inactive, Active and NotFound say what the function
/// cluster holds at rest.
public enum FunctionStatus
{
    // Never deployed: nothing of it on the function cluster.
    Inactive,
    Deploying,
    // A version runs, and the reconciler last saw its Knative Service.
    Active,
    Deleting,
    Deleted,
    // A version had run, and the reconciler no longer finds its Knative Service; never Deleted, which only a delete writes.
    NotFound,
}

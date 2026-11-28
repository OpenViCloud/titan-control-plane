namespace Titan.Domain.Aggregates.Function;

/// The one status of a function, the only one the API shows (titan-ops features/ovc-function/design.md#function-lifecycle).
/// Deploying and Deleting say an operation waits on its command; Inactive, Active and NotFound say what the function
/// cluster holds at rest.
public enum FunctionStatus
{
    // No version runs: never deployed, or every deploy so far gave up (one that gave up after Knative made the Service
    // leaves the Service, so ExternalId is set and the reconciler still reads it).
    Inactive,
    Deploying,
    // A version runs, and the reconciler last saw its Knative Service.
    Active,
    Deleting,
    Deleted,
    // A version had run, and the reconciler no longer finds its Knative Service; never Deleted, which only a delete writes.
    NotFound,
}

namespace Titan.Domain.Aggregates.FunctionRuntime;

/// The one status of a function runtime in one function cluster, the four of a VM template in a cluster
/// (titan-ops features/ovc-function/design.md#runtimes, titan-ops features/catalog/design.md#template-state-is-existence):
/// Syncing says the sync waits, Available and NotFound say whether every Ready node of the cluster holds the image at
/// rest, and Failed says a sync gave up while the image had never been seen there. Never Deleted: a runtime is not
/// removed from a cluster.
public enum FunctionRuntimeClusterStatus
{
    Syncing,
    // Every Ready node holds the image and nothing waits.
    Available,
    // The image had been there, or an observation found it missing where no sync ran.
    NotFound,
    // The sync gave up while the image had never been seen on the cluster.
    Failed
}

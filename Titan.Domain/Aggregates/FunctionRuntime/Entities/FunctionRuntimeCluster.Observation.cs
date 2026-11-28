namespace Titan.Domain.Aggregates.FunctionRuntime;

public partial class FunctionRuntimeCluster
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): the status at rest, or only
    // what is seen while a sync waits; ends no sync (titan-ops architecture/resource-lifecycle.md#invariants), and the
    // reason of the last failed sync stays.

    /// The reconciler saw the image on every Ready node.
    internal void MarkObserved()
    {
        if (SyncWaits)
        {
            Operation = Operation.Saw(FunctionRuntimeClusterStatus.Available);
        }
        else
        {
            Status = FunctionRuntimeClusterStatus.Available;
        }
    }

    /// The reconciler found a Ready node without the image (kubelet's image garbage collection can remove it). A cluster
    /// whose sync gave up before the image was ever there stays Failed; while a sync waits, an image seen there before
    /// is now missing, and one never seen stays never seen.
    internal void MarkNotFound()
    {
        if (Status == FunctionRuntimeClusterStatus.Failed) return;
        if (SyncWaits)
        {
            FunctionRuntimeClusterStatus? known = null;
            if (Operation.LastSeenStatus != null)
            {
                known = FunctionRuntimeClusterStatus.NotFound;
            }
            Operation = Operation.Saw(known);
        }
        else
        {
            Status = FunctionRuntimeClusterStatus.NotFound;
        }
    }
}

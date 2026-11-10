using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.VmTemplate;

public partial class VmTemplateCluster
{
    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): the status at rest, or only
    // what is seen while a sync waits; ends no sync (titan-ops architecture/resource-lifecycle.md#invariants), and the
    // reason of the last failed sync stays.

    /// The reconciler saw the alias. It can only have looked for one whose id Titan holds.
    internal void MarkObserved()
    {
        if (string.IsNullOrEmpty(ExternalTemplateId))
            throw new BusinessException("PROVIDER_ID_MISSING", "A template with no provider id cannot have been seen on the provider.");

        if (SyncWaits)
        {
            Operation = Operation.Saw(VmTemplateClusterStatus.Available);
        }
        else
        {
            Status = VmTemplateClusterStatus.Available;
        }
    }

    /// The reconciler did not find the alias. A cluster whose sync gave up before any alias was there stays Failed.
    internal void MarkNotFound()
    {
        if (Status == VmTemplateClusterStatus.Failed) return;
        if (SyncWaits)
        {
            Operation = Operation.Saw(null);
        }
        else
        {
            Status = VmTemplateClusterStatus.NotFound;
        }
    }
}

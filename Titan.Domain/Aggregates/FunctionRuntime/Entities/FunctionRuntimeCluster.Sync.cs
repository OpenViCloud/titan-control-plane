using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.FunctionRuntime;

public partial class FunctionRuntimeCluster
{
    /// Refused when every Ready node holds the image or a sync is waiting: the handler recognises the repeat first and
    /// sends nothing (titan-ops architecture/resource-lifecycle.md#repeat). A sync from NotFound remembers it, so a
    /// give-up rests there NotFound again rather than Failed.
    internal Guid Sync()
    {
        if (Status == FunctionRuntimeClusterStatus.Syncing)
        {
            throw new BusinessException("INVALID_STATUS", $"A sync is already waiting in cluster '{ClusterId}'.");
        }
        if (Status == FunctionRuntimeClusterStatus.Available)
        {
            throw new BusinessException("INVALID_STATUS", $"The runtime is already on cluster '{ClusterId}'.");
        }
        FunctionRuntimeClusterStatus? known = null;
        if (Status == FunctionRuntimeClusterStatus.NotFound)
        {
            known = FunctionRuntimeClusterStatus.NotFound;
        }
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, known);
        Status = FunctionRuntimeClusterStatus.Syncing;
        return operationId;
    }

    /// The sync command ended Done: every Ready node holds the image, and an older failure is cleared. Ignored,
    /// returning false, when it names another operation than the sync the cluster waits on.
    internal bool SyncSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, FunctionRuntimeClusterStatus.Syncing))
        {
            return false;
        }
        Operation = Operation.Done();
        Status = FunctionRuntimeClusterStatus.Available;
        return true;
    }

    /// The sync command gave up: records the reason, and the cluster rests on what it was last known to be (Available
    /// when the reconciler saw every Ready node hold the image meanwhile, NotFound when it had been there), Failed when
    /// the image was never seen there. Ignored, returning false, when it names another operation than the waiting sync.
    internal bool SyncFailed(Guid operationId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, FunctionRuntimeClusterStatus.Syncing))
        {
            return false;
        }
        FunctionRuntimeClusterStatus resting = Operation.LastSeenStatus ?? FunctionRuntimeClusterStatus.Failed;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }
}

using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.VmTemplate;

public partial class VmTemplateCluster
{
    /// Refused when the cluster holds the alias or a sync is waiting: the handler recognises the repeat first and sends
    /// nothing (titan-ops architecture/resource-lifecycle.md#repeat). A cluster whose alias went missing is asked again.
    /// There is no provider id to hand out (the worker names the alias), so the caller gets the sync's id for the command's
    /// key and payload.
    internal Guid Sync()
    {
        if (Status == VmTemplateClusterStatus.Syncing)
        {
            throw new BusinessException("INVALID_STATUS", $"A sync is already waiting in cluster '{ClusterId}'.");
        }
        if (Status == VmTemplateClusterStatus.Available)
        {
            throw new BusinessException("INVALID_STATUS", $"The template is already on cluster '{ClusterId}'.");
        }
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, null);
        Status = VmTemplateClusterStatus.Syncing;
        UpdatedAt = DateTimeOffset.UtcNow;
        return operationId;
    }

    /// The sync command ended Done: records the alias, the cluster is Available and an older failure is cleared. A Done
    /// must leave the cluster named on Incus, or nothing would be left to check: without an alias it is refused before
    /// anything is written (a worker that sends it has a bug, and does not retry a 422). A report that names another
    /// operation than the sync the cluster waits on is ignored and returns false.
    internal bool SyncSucceeded(Guid operationId, string? externalId)
    {
        if (!IsWaitingOn(operationId, VmTemplateClusterStatus.Syncing))
        {
            return false;
        }
        bool doneWithoutAnAlias = ExternalTemplateId == null && string.IsNullOrEmpty(externalId);
        if (doneWithoutAnAlias)
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a sync ends Done.");
        }
        RecordTheAlias(externalId);
        Operation = Operation.Done();
        Status = VmTemplateClusterStatus.Available;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }

    /// The sync command gave up: records the reason. A report that brings the alias (the worker found it) leaves the
    /// cluster Available, as the give-up of a create that finds its object is for every other kind; a cluster that
    /// already had an alias returns to what the sync last saw; a cluster with no alias at all is Failed. Ignored,
    /// returning false, when it names another operation than the sync the cluster waits on.
    internal bool SyncFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VmTemplateClusterStatus.Syncing))
        {
            return false;
        }
        RecordTheAlias(externalId);
        VmTemplateClusterStatus resting;
        if (!string.IsNullOrEmpty(externalId))
        {
            resting = VmTemplateClusterStatus.Available;
        }
        else if (ExternalTemplateId != null)
        {
            resting = InstanceStatus ?? VmTemplateClusterStatus.NotFound;
        }
        else
        {
            resting = VmTemplateClusterStatus.Failed;
        }
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        UpdatedAt = DateTimeOffset.UtcNow;
        return true;
    }
}

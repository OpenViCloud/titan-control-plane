using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.LoadBalancer;

public partial class LoadBalancer
{
    /// Follows Incus: a load balancer with a VIP is deleted through Incus, even one Incus no longer has; it is Deleting and
    /// waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm), remembering
    /// whether Incus held it. A delete replaces a waiting update: the update's report then names an operation no longer
    /// waiting, and is ignored. Refuses a repeat (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat),
    /// and a load balancer whose create still waits. Returns the operation and the VIP. A load balancer that never reached
    /// Incus is deleted here, at once, and gets no ticket (titan-ops architecture/desired-and-current.md#delete-locally).
    public OperationTicket? Delete()
    {
        if (Status == LoadBalancerStatus.Deleting)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "A delete is already in progress for this load balancer.");
        if (IsDeleted)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "The load balancer is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && Status == LoadBalancerStatus.Failed;
        if (neverReachedIncus)
        {
            Status = LoadBalancerStatus.Deleted;
            Operation = Operation.ReturnedToRest();
            return null;
        }
        if (Status == LoadBalancerStatus.Creating || ExternalId is null)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "Load balancer is not yet on Incus.");
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, InstanceStatus);
        Status = LoadBalancerStatus.Deleting;
        return OperationTicket.Of(operationId, ExternalId);
    }

    /// The delete command ended Done: the load balancer is gone. Ignored, returning false, when it names another operation
    /// than the delete the load balancer waits on.
    public bool DeleteSucceeded(Guid operationId, string? externalId)
    {
        if (!IsWaitingOn(operationId, LoadBalancerStatus.Deleting))
        {
            return false;
        }
        RecordTheVip(externalId);
        Operation = Operation.Done();
        Status = LoadBalancerStatus.Deleted;
        return true;
    }

    /// The delete command gave up: the load balancer rests again as Incus was seen holding it, with the reason; still on
    /// Incus, it counts against the quota again, and the next delete is a new operation. A delete may have replaced a
    /// waiting update, whose rows are still the ones asked: what Incus took is asked again, or desired and applied would
    /// differ with nothing waiting, and the replaced spec asked again would look like a repeat. Ignored, returning false,
    /// when it names another operation than the delete the load balancer waits on.
    public bool DeleteFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, LoadBalancerStatus.Deleting))
        {
            return false;
        }
        LoadBalancerStatus? seen = WhatIncusHeld(externalId);
        AskWhatIsApplied();
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = seen ?? LoadBalancerStatus.NotFound;
        return true;
    }
}

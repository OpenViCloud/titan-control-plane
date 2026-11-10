using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Values;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;
using Titan.Application.Exceptions;

namespace Titan.Application.Services.Compute.LoadBalancer;

/// <summary>
/// What every load balancer callback handler does with a worker's report, in one place: parse the outcome, pick the
/// domain method the status the load balancer waited in calls for, and record the outcome of a request that ended once
/// (titan-ops features/activity/design.md#request-and-outcome-names). The new routes and the old one share it, so they
/// cannot drift apart (titan-ops architecture/resource-lifecycle.md#callback-routes).
/// </summary>
public static class LoadBalancerReports
{
    // Keyed by the status the load balancer waited in, which says the operation that ended.
    private static readonly Dictionary<LoadBalancerStatus, string> DoneAction = new()
    {
        [LoadBalancerStatus.Creating] = "load_balancer.created",
        [LoadBalancerStatus.Updating] = "load_balancer.updated",
        [LoadBalancerStatus.Deleting] = "load_balancer.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<LoadBalancerStatus, string> FailedAction = new()
    {
        [LoadBalancerStatus.Updating] = "load_balancer.update_failed",
        [LoadBalancerStatus.Deleting] = "load_balancer.delete_failed",
    };


    /// Ends the load balancer's waiting operation with a Done and records its outcome once. The status the load balancer
    /// waited in says which method ends it, read before the call. Null when the report names no waiting operation:
    /// nothing is recorded; otherwise the status it waited in.
    public static LoadBalancerStatus? FinishOperation(LoadBalancerAggregate loadBalancer, Guid operationId,
        string? externalId, IActivityRecorder activity)
    {
        LoadBalancerStatus waiting = loadBalancer.Status;
        bool ended;
        switch (waiting)
        {
            case LoadBalancerStatus.Creating:
                ended = loadBalancer.CreateSucceeded(operationId, externalId);
                break;
            case LoadBalancerStatus.Updating:
                ended = loadBalancer.UpdateSucceeded(operationId, externalId);
                break;
            case LoadBalancerStatus.Deleting:
                ended = loadBalancer.DeleteSucceeded(operationId, externalId);
                break;
            default:
                // Nothing waits: the report is stale.
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        if (DoneAction.TryGetValue(waiting, out string? action))
        {
            activity.StageOutcome(loadBalancer.TenantId, action, "load_balancer", loadBalancer.Id, loadBalancer.Name);
        }
        return waiting;
    }

    /// Ends the load balancer's waiting operation with a give-up and records its outcome once. Null when the report names
    /// no waiting operation: nothing is recorded; otherwise the status it waited in.
    public static LoadBalancerStatus? FailOperation(LoadBalancerAggregate loadBalancer, Guid operationId,
        string? externalId, string? reason, string? detail, IActivityRecorder activity)
    {
        LoadBalancerStatus waiting = loadBalancer.Status;
        bool ended;
        switch (waiting)
        {
            case LoadBalancerStatus.Creating:
                ended = loadBalancer.CreateFailed(operationId, externalId, reason, detail);
                break;
            case LoadBalancerStatus.Updating:
                ended = loadBalancer.UpdateFailed(operationId, externalId, reason, detail);
                break;
            case LoadBalancerStatus.Deleting:
                ended = loadBalancer.DeleteFailed(operationId, externalId, reason, detail);
                break;
            default:
                // Nothing waits: the report is stale.
                ended = false;
                break;
        }
        if (!ended)
        {
            return null;
        }
        if (FailedAction.TryGetValue(waiting, out string? action))
        {
            activity.StageOutcome(loadBalancer.TenantId, action, "load_balancer", loadBalancer.Id, loadBalancer.Name);
        }
        return waiting;
    }
}

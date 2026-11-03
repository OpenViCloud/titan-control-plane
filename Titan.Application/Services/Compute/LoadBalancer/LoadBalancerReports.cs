using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;

namespace Titan.Application.Services.Compute.LoadBalancer;

/// <summary>
/// What every load balancer callback handler does with a worker's report, in one place: parse the outcome, and record
/// the outcome of a request that ended once (titan-ops features/activity/design.md#request-and-outcome-names). The new
/// routes and the old one share it, so they cannot drift apart (titan-ops architecture/resource-lifecycle.md#callback-routes).
/// </summary>
public static class LoadBalancerReports
{
    private static readonly Dictionary<LoadBalancerRequestKind, string> DoneAction = new()
    {
        [LoadBalancerRequestKind.Create] = "load_balancer.created",
        [LoadBalancerRequestKind.Update] = "load_balancer.updated",
        [LoadBalancerRequestKind.Delete] = "load_balancer.deleted",
    };

    // A failed create records nothing: the request was the create, and a failure is not its outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds).
    private static readonly Dictionary<LoadBalancerRequestKind, string> FailedAction = new()
    {
        [LoadBalancerRequestKind.Update] = "load_balancer.update_failed",
        [LoadBalancerRequestKind.Delete] = "load_balancer.delete_failed",
    };

    public static RequestOutcome ParseOutcome(string? outcome)
    {
        switch (outcome)
        {
            case "Done":
                return RequestOutcome.Done;
            case "Failed":
                return RequestOutcome.Failed;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown request outcome: '{outcome}'");
        }
    }

    /// Records the ended request's outcome once, under the load balancer's tenant.
    public static void RecordEnded(LoadBalancerAggregate loadBalancer, PendingRequest<LoadBalancerRequestKind> ended,
        RequestOutcome outcome, IActivityRecorder activity)
    {
        Dictionary<LoadBalancerRequestKind, string> actions = outcome == RequestOutcome.Done ? DoneAction : FailedAction;
        if (actions.TryGetValue(ended.Kind, out string? action))
        {
            activity.StageOutcome(loadBalancer.TenantId, action, "load_balancer", loadBalancer.Id, loadBalancer.Name);
        }
    }
}

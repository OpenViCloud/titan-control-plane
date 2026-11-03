using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute.LoadBalancer;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;

namespace Titan.Application.Commands.Compute.LoadBalancer;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): each
// report of a worker that predates the request routes is mapped onto the request the load balancer is waiting on.
// Authenticated by X-Internal-Token at the endpoint, so no ownership check here; the row is locked like every
// lifecycle handler's.
public class UpdateLoadBalancerStatusCommandHandler(ILoadBalancerRepository repo, IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateLoadBalancerStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)

        // Parsed before the load balancer is loaded: a malformed report is the worker's bug.
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                break;
            case "Created":
                if (string.IsNullOrEmpty(req.ExternalId))
                {
                    throw new BadRequestException("INVALID_STATUS", "ExternalId is required when report is Created.");
                }
                break;
            case "Updated":
                if (req.IntentId == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "IntentId is required when report is Updated.");
                }
                break;
            case "RequestFailed":
            case "Deleted":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown load balancer report: '{req.Report}'");
        }

        LoadBalancerAggregate loadBalancer = await repo.FindByIdForUpdateAsync(command.LoadBalancerId, ct)
            ?? throw new NotFoundException("LOAD_BALANCER_NOT_FOUND", "Load balancer not found.");

        PendingRequest<LoadBalancerRequestKind>? waiting = loadBalancer.Pending;
        PendingRequest<LoadBalancerRequestKind>? ended = null;
        RequestOutcome outcome = RequestOutcome.Done;
        switch (req.Report)
        {
            case "Observed":
                // Seeing the VIP ends nothing: only a command's own report does.
                if (req.Exists!.Value)
                {
                    loadBalancer.MarkObserved();
                }
                else
                {
                    loadBalancer.MarkNotFound();
                }
                break;
            case "Created":
                if (waiting is { Kind: LoadBalancerRequestKind.Create })
                {
                    ended = loadBalancer.EndRequest(waiting.Id, LoadBalancerRequestEnd.Done(req.ExternalId));
                }
                break;
            case "Updated":
                // The update command reports the intent it carried, which is the request's id; any other is an update a
                // delete replaced, or one already ended.
                if (waiting is { Kind: LoadBalancerRequestKind.Update } && waiting.Id == req.IntentId)
                {
                    ended = loadBalancer.EndRequest(waiting.Id, LoadBalancerRequestEnd.Done(null));
                }
                break;
            case "RequestFailed":
                // An update's give-up names its intent: one a delete has since replaced must not end the delete
                // (titan-ops architecture/desired-and-current.md#abandoned-request).
                bool namesAnotherRequest = req.IntentId != null && req.IntentId != waiting?.Id;
                if (waiting != null && !namesAnotherRequest)
                {
                    outcome = RequestOutcome.Failed;
                    ended = loadBalancer.EndRequest(waiting.Id,
                        LoadBalancerRequestEnd.Failed(req.ExternalId, req.Reason, req.Detail));
                }
                break;
            case "Deleted":
                // Only a delete's own report ends a load balancer; with no delete waiting it is gone from Incus, which is
                // what NotFound records.
                if (waiting is { Kind: LoadBalancerRequestKind.Delete })
                {
                    ended = loadBalancer.EndRequest(waiting.Id, LoadBalancerRequestEnd.Done(null));
                }
                else
                {
                    loadBalancer.MarkNotFound();
                }
                break;
        }

        if (ended != null)
        {
            LoadBalancerReports.RecordEnded(loadBalancer, ended, outcome, activity);
        }
        await repo.SaveAsync(ct);
    }
}

using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Domain.Aggregates.Compute.LoadBalancer;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using LoadBalancerAggregate = Titan.Domain.Aggregates.Compute.LoadBalancer.LoadBalancer;

namespace Titan.Application.Commands.Compute.LoadBalancer;

// Worker callback — an observation of Incus (titan-ops architecture/intent-and-observation.md#intent-versus-observation).
// No ICurrentUser: the caller is authenticated by X-Internal-Token at the endpoint. Outcomes are recorded once: the
// aggregate says whether a report fulfilled the waiting request (titan-ops features/activity/design.md#outcome-source).
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

        var loadBalancer = await repo.FindByIdAsync(command.LoadBalancerId, ct)
            ?? throw new NotFoundException("LOAD_BALANCER_NOT_FOUND", "Load balancer not found.");

        string? outcome = null;
        switch (req.Report)
        {
            case "Observed":
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
                if (loadBalancer.MarkCreated(req.ExternalId!))
                {
                    outcome = "load_balancer.created";
                }
                break;
            case "Updated":
                if (loadBalancer.MarkUpdated(req.IntentId!.Value))
                {
                    outcome = "load_balancer.updated";
                }
                break;
            case "RequestFailed":
                outcome = ApplyGiveUp(loadBalancer, req);
                break;
            case "Deleted":
                if (loadBalancer.MarkDeleted())
                {
                    outcome = "load_balancer.deleted";
                }
                break;
        }

        if (outcome != null)
        {
            activity.StageOutcome(loadBalancer.TenantId, outcome, "load_balancer", loadBalancer.Id, loadBalancer.Name);
        }
        await repo.SaveAsync(ct);
    }

    private static string? ApplyGiveUp(LoadBalancerAggregate loadBalancer, UpdateLoadBalancerStatusCommand req)
    {
        // An update's give-up names its intent: one a delete has since replaced is stale, and must not abandon the
        // delete (titan-ops architecture/desired-and-current.md#abandoned-request).
        bool isAStaleUpdate = req.IntentId != null && req.IntentId != loadBalancer.PendingIntentId;
        if (isAStaleUpdate)
        {
            return null;
        }
        // A create's give-up carrying the id found the VIP on Incus: the create happened.
        bool carriesAnIdNotYetStored = !string.IsNullOrEmpty(req.ExternalId) && loadBalancer.ExternalId == null;
        if (carriesAnIdNotYetStored && loadBalancer.MarkCreated(req.ExternalId!))
        {
            return "load_balancer.created";
        }
        bool updateOrDeleteWaiting = loadBalancer.Transition == LoadBalancerTransition.Updating
                                     || loadBalancer.Transition == LoadBalancerTransition.Deleting;
        if (updateOrDeleteWaiting)
        {
            loadBalancer.AbandonRequest(req.Reason, req.Detail);
        }
        else
        {
            loadBalancer.MarkRequestFailed(req.Reason, req.Detail);
        }
        return null;
    }
}

using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute.LoadBalancer;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;
using Titan.Application.Services;

namespace Titan.Application.Commands.Compute.LoadBalancer;

// The worker's report that a load balancer request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes). Authenticated by X-Internal-Token at the endpoint,
// acting for no tenant. The row is locked so a request made meanwhile is ordered before or after this report, never
// interleaved (titan-ops architecture/resource-lifecycle.md#one-key-per-request). A report the load balancer is not
// waiting on is ignored and still answers 204: the worker has nothing to retry.
public class EndLoadBalancerRequestCommandHandler(ILoadBalancerRepository repo, IActivityRecorder activity)
{
    public async Task HandleAsync(EndLoadBalancerRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the load balancer is loaded: a malformed report is the worker's bug, whatever the load balancer.
        OperationOutcome outcome = LoadBalancerReports.ParseOutcome(command.Outcome);

        LoadBalancerAggregate loadBalancer = await repo.FindByIdForUpdateAsync(command.LoadBalancerId, ct)
            ?? throw new NotFoundException("LOAD_BALANCER_NOT_FOUND", "Load balancer not found.");

        LoadBalancerStatus? ended;
        if (outcome == OperationOutcome.Done)
        {
            ended = LoadBalancerReports.FinishOperation(loadBalancer, command.RequestId, command.ExternalId, activity);
        }
        else
        {
            ended = LoadBalancerReports.FailOperation(loadBalancer, command.RequestId, command.ExternalId,
                command.Reason, command.Detail, activity);
        }
        if (ended == null)
        {
            return;
        }

        await repo.SaveAsync(ct);
    }
}

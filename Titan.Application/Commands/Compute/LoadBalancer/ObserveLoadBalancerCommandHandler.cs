using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using LoadBalancerAggregate = Titan.Domain.Aggregates.LoadBalancer.LoadBalancer;

namespace Titan.Application.Commands.Compute.LoadBalancer;

// The reconciler's observation of one load balancer (titan-ops architecture/resource-lifecycle.md#callback-routes): what
// Incus has, written to current; no request ends here (titan-ops architecture/resource-lifecycle.md#invariants).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant. Locked like every lifecycle handler, so an
// observation and a request on the same load balancer take turns.
public class ObserveLoadBalancerCommandHandler(ILoadBalancerRepository repo)
{
    public async Task HandleAsync(ObserveLoadBalancerCommand command, CancellationToken ct = default)
    {
        LoadBalancerAggregate loadBalancer = await repo.FindByIdForUpdateAsync(command.LoadBalancerId, ct)
            ?? throw new NotFoundException("LOAD_BALANCER_NOT_FOUND", "Load balancer not found.");

        if (command.Exists)
        {
            loadBalancer.MarkObserved();
        }
        else
        {
            loadBalancer.MarkNotFound();
        }
        await repo.SaveAsync(ct);
    }
}

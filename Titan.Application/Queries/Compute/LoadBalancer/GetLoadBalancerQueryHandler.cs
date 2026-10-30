using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.LoadBalancer;

/// A Deleted load balancer is still answered, like a Deleted network.
public class GetLoadBalancerQueryHandler(ILoadBalancerRepository repo, ICurrentUser currentUser)
{
    public async Task<LoadBalancerQueryResult> HandleAsync(GetLoadBalancerQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var loadBalancer = await ComputeOwnership.RequireLoadBalancerAsync(repo, tenantId, query.LoadBalancerId, ct);
        return LoadBalancerQueryResult.From(loadBalancer);
    }
}

using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.LoadBalancer.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.LoadBalancer;

public class ListLoadBalancersQueryHandler(ILoadBalancerRepository repo, ICurrentUser currentUser)
{
    public async Task<List<LoadBalancerQueryResult>> HandleAsync(ListLoadBalancersQuery query, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var loadBalancers = await repo.ListByTenantAsync(tenantId, ct);
        return loadBalancers.Select(LoadBalancerQueryResult.From).ToList();
    }
}

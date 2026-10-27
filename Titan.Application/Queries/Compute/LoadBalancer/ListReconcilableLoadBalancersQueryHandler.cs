using Titan.Application.Queries.Compute.LoadBalancer.Contracts;
using Titan.Domain.Repositories.Compute;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Queries.Compute.LoadBalancer;

// Read side of the load balancer reconciler. No ICurrentUser: the reconciler acts for no tenant; the endpoint
// authenticates it by X-Internal-Token.
public class ListReconcilableLoadBalancersQueryHandler(ILoadBalancerRepository repo)
{
    public async Task<List<ReconcilableLoadBalancerQueryResult>> HandleAsync(
        ListReconcilableLoadBalancersQuery query, CancellationToken ct = default)
    {
        var rows = await repo.ListReconcilableAsync(query.ClusterId, ct);
        return rows.Select(lb => new ReconcilableLoadBalancerQueryResult(
                lb.Id, TenantAggregate.ProjectNameFor(lb.TenantId), lb.ExternalNetworkId, lb.Vip,
                lb.CurrentExistence.ToString()))
            .ToList();
    }
}

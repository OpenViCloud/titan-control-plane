using Titan.Application.Queries.Compute.LoadBalancer.Contracts;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Queries.Compute.LoadBalancer;

// Read side of the load balancer reconciler. currentExistence is the worker's own word: Exists when Titan last knew Incus
// to hold the VIP (InstanceStatus), NotFound otherwise, a delete in flight included. No ICurrentUser: the reconciler
// acts for no tenant; the endpoint authenticates it by X-Internal-Token.
public class ListReconcilableLoadBalancersQueryHandler(ILoadBalancerRepository repo)
{
    public async Task<List<ReconcilableLoadBalancerQueryResult>> HandleAsync(
        ListReconcilableLoadBalancersQuery query, CancellationToken ct = default)
    {
        var rows = await repo.ListReconcilableAsync(query.ClusterId, ct);
        return rows.Select(lb => new ReconcilableLoadBalancerQueryResult(
                lb.Id, TenantAggregate.ProjectNameFor(lb.TenantId), lb.ExternalNetworkId, lb.Vip,
                lb.InstanceStatus == LoadBalancerStatus.Available ? "Exists" : "NotFound"))
            .ToList();
    }
}

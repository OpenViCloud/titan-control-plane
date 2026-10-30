using Titan.Domain.Aggregates.LoadBalancer;

namespace Titan.Domain.Repositories;

public interface ILoadBalancerRepository
{
    /// With its listeners and backends.
    Task<LoadBalancer?> FindByIdAsync(Guid id, CancellationToken ct = default);
    /// Not Deleted, newest first, with listeners and backends.
    Task<List<LoadBalancer>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// MaxLoadBalancers (titan-ops features/load-balancer/design.md#lb-quota-and-billing): desired Exists and Incus has it
    /// or the create waits.
    Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// Same condition as the unique index on (tenant_id, name).
    Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default);
    /// The VIP numbers live (not Deleted) load balancers of the network hold: same rule as the unique index.
    Task<HashSet<int>> ListVipIndexesOfNetworkAsync(Guid networkId, CancellationToken ct = default);
    /// A load balancer not yet Deleted asks for this VM, or Incus still sends to it.
    Task<bool> IsBackendAsync(Guid vmId, CancellationToken ct = default);
    /// The reconciler's set: a provider id, not yet Deleted.
    Task<List<LoadBalancer>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    void Add(LoadBalancer loadBalancer);
    Task SaveAsync(CancellationToken ct = default);
}

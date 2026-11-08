using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Repositories;

namespace Titan.Tests.Fakes;

// In-memory ILoadBalancerRepository for unit tests (titan-ops architecture/testing.md#doubles-only-in-unit-tests). The
// quota count asks LoadBalancer.OccupiesQuota of each row, the bool whose SQL twin LoadBalancerRepository writes; the
// other queries mirror its conditions. Postgres-only behaviour (unique indexes) is LoadBalancerDbTests'.
public class FakeLoadBalancerRepository : ILoadBalancerRepository
{
    public List<LoadBalancer> Rows { get; } = new();
    public int Saves { get; private set; }

    public Task<LoadBalancer?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(lb => lb.Id == id));

    /// No rows to lock in memory; counted, so a test can see the handler asked for the lock.
    public int LockedReads { get; private set; }

    public Task<LoadBalancer?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        LockedReads++;
        return FindByIdAsync(id, ct);
    }

    public Task<List<LoadBalancer>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(lb => lb.TenantId == tenantId && lb.Status != LoadBalancerStatus.Deleted)
            .OrderByDescending(lb => lb.CreatedAt).ToList());

    public Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Count(lb => lb.TenantId == tenantId && lb.OccupiesQuota));

    public Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        Task.FromResult(Rows.Any(lb => lb.TenantId == tenantId && lb.Name == name && lb.Status != LoadBalancerStatus.Deleted));

    public Task<HashSet<int>> ListVipIndexesOfNetworkAsync(Guid networkId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(lb => lb.NetworkId == networkId && lb.Status != LoadBalancerStatus.Deleted)
            .Select(lb => lb.VipIndex).ToHashSet());

    public Task<bool> IsBackendAsync(Guid vmId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Any(lb => lb.Status != LoadBalancerStatus.Deleted && lb.Backends.Any(b => b.VmId == vmId)));

    public Task<List<LoadBalancer>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(lb => lb.ClusterId == clusterId && lb.ExternalId != null && lb.Status != LoadBalancerStatus.Deleted).ToList());

    public void Add(LoadBalancer loadBalancer) => Rows.Add(loadBalancer);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saves++;
        return Task.CompletedTask;
    }
}

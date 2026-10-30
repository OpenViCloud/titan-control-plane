using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Tests.Fakes;

// In-memory ILoadBalancerRepository for unit tests (titan-ops architecture/testing.md#doubles-only-in-unit-tests). The
// queries mirror LoadBalancerRepository's conditions; Postgres-only behaviour (unique indexes) is
// LoadBalancerDbTests'.
public class FakeLoadBalancerRepository : ILoadBalancerRepository
{
    public List<LoadBalancer> Rows { get; } = new();
    public int Saves { get; private set; }

    public Task<LoadBalancer?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(lb => lb.Id == id));

    public Task<List<LoadBalancer>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(lb => lb.TenantId == tenantId && lb.CurrentExistence != CurrentExistence.Deleted)
            .OrderByDescending(lb => lb.CreatedAt).ToList());

    public Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Count(lb => lb.TenantId == tenantId
                                         && lb.DesiredExistence == DesiredExistence.Exists
                                         && (lb.CurrentExistence == CurrentExistence.Exists
                                             || (lb.CurrentExistence == CurrentExistence.NotCreated && lb.PendingIntentId != null))));

    public Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        Task.FromResult(Rows.Any(lb => lb.TenantId == tenantId && lb.Name == name && lb.CurrentExistence != CurrentExistence.Deleted));

    public Task<HashSet<int>> ListVipIndexesOfNetworkAsync(Guid networkId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(lb => lb.NetworkId == networkId && lb.CurrentExistence != CurrentExistence.Deleted)
            .Select(lb => lb.VipIndex).ToHashSet());

    public Task<bool> IsBackendAsync(Guid vmId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Any(lb => lb.CurrentExistence != CurrentExistence.Deleted && lb.Backends.Any(b => b.VmId == vmId)));

    public Task<List<LoadBalancer>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        Task.FromResult(Rows.Where(lb => lb.ClusterId == clusterId && lb.ExternalId != null && lb.CurrentExistence != CurrentExistence.Deleted).ToList());

    public void Add(LoadBalancer loadBalancer) => Rows.Add(loadBalancer);

    public Task SaveAsync(CancellationToken ct = default)
    {
        Saves++;
        return Task.CompletedTask;
    }
}

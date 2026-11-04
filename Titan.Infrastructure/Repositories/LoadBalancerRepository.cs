using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

// Every state condition below is the SQL twin of a plain bool on LoadBalancer
// (titan-ops architecture/domain-methods.md#plain-code); LoadBalancerRepositoryStateTests runs every shape through both,
// and QueryRuleDbTests pins each side of the quota condition on Postgres.
public class LoadBalancerRepository(TitanDbContext db) : ILoadBalancerRepository
{
    private IQueryable<LoadBalancer> WithRows() =>
        db.LoadBalancers.Include(lb => lb.Listeners).Include(lb => lb.Backends);

    public async Task<LoadBalancer?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await WithRows().FirstOrDefaultAsync(lb => lb.Id == id, ct);

    public async Task<LoadBalancer?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.load_balancers WHERE id = {id} FOR UPDATE", ct);
        return await FindByIdAsync(id, ct);
    }

    // LoadBalancer.IsNotDeleted
    public async Task<List<LoadBalancer>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await WithRows()
            .Where(lb => lb.TenantId == tenantId && lb.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .OrderByDescending(lb => lb.CreatedAt)
            .ToListAsync(ct);

    // LoadBalancer.OccupiesQuota
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.LoadBalancers.CountAsync(
            lb => lb.TenantId == tenantId
                  && lb.DesiredExistence == DesiredExistence.Exists
                  && (lb.CurrentExistence == CurrentExistence.Exists
                      || (lb.CurrentExistence == CurrentExistence.NotCreated && lb.PendingRequestId != null)), ct);

    // LoadBalancer.IsNotDeleted
    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.LoadBalancers.AnyAsync(
            lb => lb.TenantId == tenantId && lb.Name == name && lb.CurrentExistence != CurrentExistence.Deleted, ct);

    // LoadBalancer.IsNotDeleted
    public async Task<HashSet<int>> ListVipIndexesOfNetworkAsync(Guid networkId, CancellationToken ct = default)
    {
        var indexes = await db.LoadBalancers
            .Where(lb => lb.NetworkId == networkId && lb.CurrentExistence != CurrentExistence.Deleted)
            .Select(lb => lb.VipIndex)
            .ToListAsync(ct);
        return indexes.ToHashSet();
    }

    // LoadBalancer.IsNotDeleted
    public async Task<bool> IsBackendAsync(Guid vmId, CancellationToken ct = default) =>
        await db.LoadBalancers.AnyAsync(
            lb => lb.CurrentExistence != CurrentExistence.Deleted && lb.Backends.Any(b => b.VmId == vmId), ct);

    // LoadBalancer.IsNotDeleted with a provider id.
    public async Task<List<LoadBalancer>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.LoadBalancers
            .Where(lb => lb.ClusterId == clusterId && lb.ExternalId != null && lb.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(LoadBalancer loadBalancer) => db.LoadBalancers.Add(loadBalancer);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

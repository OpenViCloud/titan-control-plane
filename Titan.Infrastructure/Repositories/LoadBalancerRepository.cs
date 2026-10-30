using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class LoadBalancerRepository(TitanDbContext db) : ILoadBalancerRepository
{
    private IQueryable<LoadBalancer> WithRows() =>
        db.LoadBalancers.Include(lb => lb.Listeners).Include(lb => lb.Backends);

    public async Task<LoadBalancer?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await WithRows().FirstOrDefaultAsync(lb => lb.Id == id, ct);

    public async Task<List<LoadBalancer>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await WithRows()
            .Where(lb => lb.TenantId == tenantId && lb.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .OrderByDescending(lb => lb.CreatedAt)
            .ToListAsync(ct);

    // titan-ops features/load-balancer/design.md#lb-quota-and-billing: what Incus holds or is about to, unless its delete
    // was pressed. A create that failed before Incus had it, or one Incus no longer has, does not count.
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.LoadBalancers.CountAsync(
            lb => lb.TenantId == tenantId
                  && lb.DesiredExistence == DesiredExistence.Exists
                  && (lb.CurrentExistence == CurrentExistence.Exists
                      || (lb.CurrentExistence == CurrentExistence.NotCreated && lb.PendingIntentId != null)), ct);

    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
        await db.LoadBalancers.AnyAsync(
            lb => lb.TenantId == tenantId && lb.Name == name && lb.CurrentExistence != CurrentExistence.Deleted, ct);

    public async Task<HashSet<int>> ListVipIndexesOfNetworkAsync(Guid networkId, CancellationToken ct = default)
    {
        var indexes = await db.LoadBalancers
            .Where(lb => lb.NetworkId == networkId && lb.CurrentExistence != CurrentExistence.Deleted)
            .Select(lb => lb.VipIndex)
            .ToListAsync(ct);
        return indexes.ToHashSet();
    }

    public async Task<bool> IsBackendAsync(Guid vmId, CancellationToken ct = default) =>
        await db.LoadBalancers.AnyAsync(
            lb => lb.CurrentExistence != CurrentExistence.Deleted && lb.Backends.Any(b => b.VmId == vmId), ct);

    public async Task<List<LoadBalancer>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.LoadBalancers
            .Where(lb => lb.ClusterId == clusterId && lb.ExternalId != null && lb.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(LoadBalancer loadBalancer) => db.LoadBalancers.Add(loadBalancer);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

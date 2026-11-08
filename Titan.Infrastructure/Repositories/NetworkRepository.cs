using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

// Every state condition below is the SQL twin of a plain bool on Network, Vm or LoadBalancer
// (titan-ops architecture/domain-methods.md#plain-code); NetworkRepositoryStateTests and VmRepositoryStateTests run
// every shape through both; QueryRuleDbTests pins each side of the quota and held-network conditions on Postgres, and
// NetworkRequestedCidrDbTests the held range.
public class NetworkRepository(TitanDbContext db) : INetworkRepository
{
    public async Task<Network?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Networks.Include(n => n.FirewallRules).FirstOrDefaultAsync(n => n.Id == id, ct);

    // Lock first, load second: one statement joining the rules under READ COMMITTED would read the locked row at its
    // latest version but the rules at the statement's snapshot, so a rule the other writer just committed could be
    // missed. The second statement starts after the lock is held and sees it (titan-ops architecture/code-structure.md#child-entity).
    public async Task<Network?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default)
    {
        await db.LockRowAsync($"SELECT id FROM core.networks WHERE id = {id} FOR UPDATE", ct);
        return await FindByIdAsync(id, ct);
    }

    public async Task<Network?> FindByFirewallRuleIdAsync(Guid ruleId, CancellationToken ct = default) =>
        await db.Networks.Include(n => n.FirewallRules)
            .FirstOrDefaultAsync(n => n.FirewallRules.Any(r => r.Id == ruleId), ct);

    // !Network.IsDeleted
    public async Task<List<Network>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.TenantId == tenantId && n.Status != NetworkStatus.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Network.OccupiesQuota: a network being deleted stops counting against the quota; its name and CIDR stay taken
    // until the delete command confirms it gone (titan-ops features/network/design.md#network-name-reuse).
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Networks.CountAsync(
            n => n.TenantId == tenantId
                 && n.Status != NetworkStatus.Deleting
                 && n.Status != NetworkStatus.Deleted
                 && n.Status != NetworkStatus.NotFound
                 && n.Status != NetworkStatus.Failed, ct);

    // Network.OccupiesPublicIpv6Quota
    public async Task<int> CountPublicIpv6ActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Networks.CountAsync(
            n => n.TenantId == tenantId
                 && n.PublicIpv6Subnet != null
                 && n.Status != NetworkStatus.Deleting
                 && n.Status != NetworkStatus.Deleted
                 && n.Status != NetworkStatus.NotFound
                 && n.Status != NetworkStatus.Failed, ct);

    // !Network.IsDeleted
    public async Task<bool> ExistsByNameAsync(Guid tenantId, NetworkName name, CancellationToken ct = default) =>
        await db.Networks.AnyAsync(
            n => n.TenantId == tenantId && n.Name == name && n.Status != NetworkStatus.Deleted, ct);

    // Vm.HasNetwork, asked of the network's VMs.
    public async Task<bool> HasActiveVmsAsync(Guid networkId, CancellationToken ct = default) =>
        await db.Set<Domain.Aggregates.Vm.Vm>().AnyAsync(
            v => v.NetworkId == networkId
                 && v.Status != Domain.Aggregates.Vm.VmStatus.Deleted
                 && v.Status != Domain.Aggregates.Vm.VmStatus.NotFound
                 && v.Status != Domain.Aggregates.Vm.VmStatus.Failed, ct);

    // !LoadBalancer.IsDeleted. Read here, not through a Compute repository: Networking -> Compute in Titan.Application
    // would close a module cycle (titan-ops architecture/code-structure.md#module-boundary), as HasActiveVmsAsync above
    // reads the VM table.
    public async Task<bool> HasLiveLoadBalancersAsync(Guid networkId, CancellationToken ct = default) =>
        await db.Set<Domain.Aggregates.LoadBalancer.LoadBalancer>().AnyAsync(
            lb => lb.NetworkId == networkId && lb.Status != Domain.Aggregates.LoadBalancer.LoadBalancerStatus.Deleted, ct);

    // Network.HoldsARange narrows the rows; Network.HeldRange is then asked of each. The rows are bounded by MaxNetworks
    // plus the deletes in flight, which keep their range but no longer count.
    public async Task<bool> HasCidrOverlapAsync(Guid tenantId, Ipv4Cidr cidr, CancellationToken ct = default)
    {
        List<Network> holding = await db.Networks
            .Where(n => n.TenantId == tenantId
                        && (n.Status == NetworkStatus.Creating
                            || n.Status == NetworkStatus.Pending
                            || n.Status == NetworkStatus.Created
                            || n.Status == NetworkStatus.Errored
                            || n.Status == NetworkStatus.Unavailable
                            || n.Status == NetworkStatus.Unknown
                            || (n.Status == NetworkStatus.Deleting && n.Operation.LastSeenStatus != null)))
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (Network network in holding)
        {
            Ipv4Cidr? range = network.HeldRange;
            if (range != null && range.Overlaps(cidr))
            {
                return true;
            }
        }
        return false;
    }

    // !Network.IsDeleted. titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read: tenantId == null spans all tenants.
    public async Task<List<Network>> ListAllAsync(Guid? tenantId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.Status != NetworkStatus.Deleted && (tenantId == null || n.TenantId == tenantId))
            .OrderBy(n => n.TenantId).ThenBy(n => n.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    // !Network.IsDeleted with a provider id.
    public async Task<List<Network>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.ClusterId == clusterId
                        && n.ExternalId != null
                        && n.Status != NetworkStatus.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(Network network) => db.Networks.Add(network);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

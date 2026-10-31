using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

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

    public async Task<List<Network>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.TenantId == tenantId && n.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Counted by occupancy: what Incus holds or is about to hold. A network being deleted stops counting against
    // the quota; its name and CIDR stay taken until the delete command confirms it gone
    // (titan-ops features/network/design.md#network-name-reuse).
    public async Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Networks.CountAsync(
            n => n.TenantId == tenantId &&
                 n.DesiredExistence == DesiredExistence.Exists &&
                 (n.CurrentExistence == CurrentExistence.Exists
                  || (n.CurrentExistence == CurrentExistence.NotCreated && n.PendingIntentId != null)), ct);

    public async Task<int> CountPublicIpv6ActiveByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Networks.CountAsync(
            n => n.TenantId == tenantId &&
                 n.PublicIpv6Index != null &&
                 n.DesiredExistence == DesiredExistence.Exists &&
                 (n.CurrentExistence == CurrentExistence.Exists
                  || (n.CurrentExistence == CurrentExistence.NotCreated && n.PendingIntentId != null)), ct);

    // Like the name, a /64 stays taken until the delete command confirms the network gone: Incus still has its
    // router until then. Same condition as the unique index in NetworkConfiguration.
    public async Task<HashSet<int>> ListPublicIpv6IndexesInClusterAsync(string clusterId, CancellationToken ct = default)
    {
        var indexes = await db.Networks
            .Where(n => n.ClusterId == clusterId && n.PublicIpv6Index != null && n.CurrentExistence != CurrentExistence.Deleted)
            .Select(n => n.PublicIpv6Index!.Value)
            .ToListAsync(ct);
        return indexes.ToHashSet();
    }

    public async Task<bool> ExistsByNameAsync(Guid tenantId, NetworkName name, CancellationToken ct = default) =>
        await db.Networks.AnyAsync(
            n => n.TenantId == tenantId && n.Name == name && n.CurrentExistence != CurrentExistence.Deleted, ct);

    // A VM holds the network while its instance exists on the provider (whatever its status, a delete in flight
    // included: Incus refuses to delete a network an instance still uses) or its create is still waiting.
    public async Task<Dictionary<int, DateTimeOffset?>> ListPublicIpv6ReleasesInClusterAsync(string clusterId,
        CancellationToken ct = default)
    {
        var releases = await db.Networks
            .Where(n => n.ClusterId == clusterId && n.PublicIpv6Index != null && n.CurrentExistence == CurrentExistence.Deleted)
            .GroupBy(n => n.PublicIpv6Index!.Value)
            .Select(g => new { Index = g.Key, ReleasedAt = g.Max(n => n.PublicIpv6ReleasedAt) })
            .ToListAsync(ct);
        return releases.ToDictionary(r => r.Index, r => r.ReleasedAt);
    }

    public async Task<bool> HasActiveVmsAsync(Guid networkId, CancellationToken ct = default) =>
        await db.Set<Domain.Aggregates.Vm.Vm>().AnyAsync(
            v => v.NetworkId == networkId &&
                 (v.CurrentExistence == CurrentExistence.Exists
                  || (v.CurrentExistence == CurrentExistence.NotCreated && v.PendingIntentId != null)), ct);

    // Read here, not through a Compute repository: Networking -> Compute in Titan.Application would close a module
    // cycle (titan-ops architecture/code-structure.md#module-boundary), as HasActiveVmsAsync above reads the VM table.
    public async Task<bool> HasLiveLoadBalancersAsync(Guid networkId, CancellationToken ct = default) =>
        await db.Set<Domain.Aggregates.LoadBalancer.LoadBalancer>().AnyAsync(
            lb => lb.NetworkId == networkId && lb.CurrentExistence != CurrentExistence.Deleted, ct);

    public async Task<bool> HasCidrOverlapAsync(Guid tenantId, Ipv4Cidr cidr, CancellationToken ct = default)
    {
        // A network Incus holds keeps the range the worker reported for it (the customer's string, or the one Incus
        // picked), a delete in flight included: reusing it could collide on Incus. One Incus is about to hold keeps
        // the range the customer asked for (titan-ops features/network/user-stories.md#us-net-intent-02). A network
        // without either holds none; a reported address that is not an IPv4 CIDR is kept verbatim and holds none here.
        var held = await db.Networks
            .Where(n => n.TenantId == tenantId
                     && (n.CurrentExistence == CurrentExistence.Exists
                         || (n.CurrentExistence == CurrentExistence.NotCreated && n.PendingIntentId != null)))
            .Select(n => new { n.CurrentExistence, n.Cidr, n.RequestedCidr })
            .ToListAsync(ct);

        foreach (var network in held)
        {
            Ipv4Cidr? range;
            if (network.CurrentExistence == CurrentExistence.Exists)
            {
                Ipv4Cidr.TryParse(network.Cidr, out range);
            }
            else
            {
                range = network.RequestedCidr;
            }
            if (range != null && range.Overlaps(cidr))
            {
                return true;
            }
        }
        return false;
    }

    // titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read. tenantId == null spans all tenants.
    public async Task<List<Network>> ListAllAsync(Guid? tenantId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.CurrentExistence != CurrentExistence.Deleted && (tenantId == null || n.TenantId == tenantId))
            .OrderBy(n => n.TenantId).ThenBy(n => n.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<ReconcilableNetwork>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.ClusterId == clusterId
                        && n.ExternalId != null
                        && n.CurrentExistence != CurrentExistence.Deleted)
            // The filter above keeps only rows with an id.
            .Select(n => new ReconcilableNetwork(n.Id, n.ExternalId!, n.CurrentState, n.CurrentExistence))
            .ToListAsync(ct);

    public void Add(Network network) => db.Networks.Add(network);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

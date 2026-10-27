using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Domain.Repositories.Networking;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class NetworkRepository(TitanDbContext db) : INetworkRepository
{
    public async Task<Network?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.Networks.FirstOrDefaultAsync(n => n.Id == id, ct);

    public async Task<List<Network>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.TenantId == tenantId && n.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Counted by occupancy: what Incus holds or is about to hold. A network being deleted stops counting against
    // the quota; its name and CIDR stay taken until the delete command confirms it gone
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/network/design.md#network-name-reuse).
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

    public async Task<bool> ExistsByNameAsync(Guid tenantId, string name, CancellationToken ct = default) =>
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
        await db.Set<Domain.Aggregates.Compute.Vm.Vm>().AnyAsync(
            v => v.NetworkId == networkId &&
                 (v.CurrentExistence == CurrentExistence.Exists
                  || (v.CurrentExistence == CurrentExistence.NotCreated && v.PendingIntentId != null)), ct);

    // Read here, not through a Compute repository: Networking -> Compute in Titan.Application would close a module
    // cycle (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#module-boundary), as HasActiveVmsAsync above reads the VM table.
    public async Task<bool> HasLiveLoadBalancersAsync(Guid networkId, CancellationToken ct = default) =>
        await db.Set<Domain.Aggregates.Compute.LoadBalancer.LoadBalancer>().AnyAsync(
            lb => lb.NetworkId == networkId && lb.CurrentExistence != CurrentExistence.Deleted, ct);

    public async Task<bool> HasCidrOverlapAsync(Guid tenantId, string cidr, CancellationToken ct = default)
    {
        // Whatever the person asked for, a network Incus holds or is about to hold keeps its range, a delete in
        // flight included: reusing it could collide on Incus. A network created without a CIDR is stored with an
        // empty one and holds no range.
        var existingCidrs = await db.Networks
            .Where(n => n.TenantId == tenantId
                     && n.Cidr != null
                     && n.Cidr != ""
                     && (n.CurrentExistence == CurrentExistence.Exists
                         || (n.CurrentExistence == CurrentExistence.NotCreated && n.PendingIntentId != null)))
            .Select(n => n.Cidr!)
            .ToListAsync(ct);

        return existingCidrs.Any(existing => CidrsOverlap(existing, cidr));
    }

    private static bool CidrsOverlap(string cidr1, string cidr2)
    {
        static (uint Start, uint End) ParseCidr(string cidr)
        {
            var parts = cidr.Split('/');
            var ip = parts[0].Split('.').Select(byte.Parse).ToArray();
            var prefix = int.Parse(parts[1]);
            var addr = (uint)(ip[0] << 24 | ip[1] << 16 | ip[2] << 8 | ip[3]);
            var mask = prefix == 0 ? 0u : ~0u << (32 - prefix);
            return (addr & mask, (addr & mask) | ~mask);
        }

        var (s1, e1) = ParseCidr(cidr1);
        var (s2, e2) = ParseCidr(cidr2);
        return s1 <= e2 && s2 <= e1;
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#admin-cross-tenant-read. tenantId == null spans all tenants.
    public async Task<List<Network>> ListAllAsync(Guid? tenantId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.CurrentExistence != CurrentExistence.Deleted && (tenantId == null || n.TenantId == tenantId))
            .OrderBy(n => n.TenantId).ThenBy(n => n.Name)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<Network>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.Networks
            .Where(n => n.ClusterId == clusterId
                        && n.ExternalId != null
                        && n.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public void Add(Network network) => db.Networks.Add(network);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

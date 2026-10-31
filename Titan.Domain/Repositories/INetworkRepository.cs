using Titan.Domain.Aggregates.Network;

using Titan.Domain.Values;

namespace Titan.Domain.Repositories;

public interface INetworkRepository
{
    /// The aggregate whole: the network with its firewall rules, tracked (titan-ops architecture/code-structure.md#child-entity).
    Task<Network?> FindByIdAsync(Guid id, CancellationToken ct = default);
    /// The same, with the network's row locked until this context's next save commits: the per-network rule cap is
    /// counted on the loaded rules, so two requests adding a rule at the cap take turns (titan-ops architecture/code-structure.md#child-entity).
    Task<Network?> FindByIdForUpdateAsync(Guid id, CancellationToken ct = default);
    /// The network a rule belongs to, with its rules, tracked; null when no network has the rule. For the worker's
    /// callbacks, which name the rule.
    Task<Network?> FindByFirewallRuleIdAsync(Guid ruleId, CancellationToken ct = default);
    Task<List<Network>> ListByTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<int> CountActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// Networks of the tenant with a public /64, counted like CountActiveByTenantAsync (MaxPublicIpv6).
    Task<int> CountPublicIpv6ActiveByTenantAsync(Guid tenantId, CancellationToken ct = default);
    /// The /64 numbers live networks of the cluster hold, any tenant: same rule as the unique index on them.
    Task<HashSet<int>> ListPublicIpv6IndexesInClusterAsync(string clusterId, CancellationToken ct = default);
    /// Each /64 number a Deleted network of the cluster held, any tenant, with the latest time it was given back (null:
    /// deleted before that was recorded).
    Task<Dictionary<int, DateTimeOffset?>> ListPublicIpv6ReleasesInClusterAsync(string clusterId, CancellationToken ct = default);
    Task<bool> ExistsByNameAsync(Guid tenantId, NetworkName name, CancellationToken ct = default);
    Task<bool> HasActiveVmsAsync(Guid networkId, CancellationToken ct = default);
    /// A load balancer of the network is not yet Deleted: it lives on the network on Incus.
    Task<bool> HasLiveLoadBalancersAsync(Guid networkId, CancellationToken ct = default);
    Task<bool> HasCidrOverlapAsync(Guid tenantId, string cidr, CancellationToken ct = default);
    /// Admin cross-tenant read (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read): every non-Deleted
    /// network, or one tenant's when tenantId is given. Not scoped to a caller.
    Task<List<Network>> ListAllAsync(Guid? tenantId, CancellationToken ct = default);
    /// Reached the provider (ExternalId set) and not yet confirmed gone, whatever Incus last said: an Errored network
    /// can come back Created, and a refused delete can still end with the network gone.
    Task<List<Network>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    void Add(Network network);
    Task SaveAsync(CancellationToken ct = default);
}

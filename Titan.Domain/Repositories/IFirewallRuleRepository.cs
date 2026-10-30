using Titan.Domain.Aggregates.FirewallRule;

namespace Titan.Domain.Repositories;

public interface IFirewallRuleRepository
{
    Task<FirewallRule?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<FirewallRule>> ListByNetworkAsync(Guid networkId, CancellationToken ct = default);
    Task<int> CountByNetworkAsync(Guid networkId, CancellationToken ct = default);
    /// Rules on the cluster's networks the firewall reconciler observes: they have a provider id, are not Deleted, and
    /// their network is not being deleted (its delete takes the ACL with it).
    Task<List<FirewallRule>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    /// The network's rules that are not Deleted, tracked: its delete marks them Deleted in the same save.
    Task<List<FirewallRule>> ListLiveOfNetworkForUpdateAsync(Guid networkId, CancellationToken ct = default);
    void Add(FirewallRule rule);
    Task SaveAsync(CancellationToken ct = default);
}

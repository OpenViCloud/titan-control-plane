using Titan.Domain.Aggregates.Network;

namespace Titan.Domain.Repositories;

/// Read side only (titan-ops architecture/code-structure.md#child-entity): a rule is written through its network
/// (INetworkRepository), which loads and saves the aggregate whole. These lists read the rule table directly so the
/// customer's list and the reconciler's pool do not load every network.
public interface IFirewallRuleRepository
{
    Task<List<FirewallRule>> ListByNetworkAsync(Guid networkId, CancellationToken ct = default);
    /// Rules on the cluster's networks the firewall reconciler observes: they have a provider id and are not Deleted,
    /// whatever their network waits on (a network's delete may give up and leave its rules in force).
    Task<List<FirewallRule>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
}

using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Read side of the rule table (titan-ops architecture/code-structure.md#child-entity); writes go through NetworkRepository.
/// Every state condition is the SQL twin of !FirewallRule.IsDeleted (titan-ops architecture/domain-methods.md#plain-code).
public class FirewallRuleRepository(TitanDbContext db) : IFirewallRuleRepository
{
    // !FirewallRule.IsDeleted. A Deleted row is kept (titan-ops features/network/design.md#firewall-rule-soft-delete)
    // but not listed. A rule whose delete is waiting is still listed: Incus still enforces it.
    public async Task<List<FirewallRule>> ListByNetworkAsync(Guid networkId, CancellationToken ct = default) =>
        await db.FirewallRules
            .Where(r => r.NetworkId == networkId && r.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // !FirewallRule.IsDeleted with a provider id. A rule of a network being deleted is still observed
    // (titan-ops architecture/resource-lifecycle.md#reconciler): the network's delete may give up and leave it in force.
    public async Task<List<FirewallRule>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.FirewallRules
            .Where(r => r.ExternalId != null
                     && r.CurrentExistence != CurrentExistence.Deleted
                     && db.Networks.Any(n => n.Id == r.NetworkId && n.ClusterId == clusterId))
            .AsNoTracking()
            .ToListAsync(ct);
}

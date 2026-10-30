using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Read side of the rule table (titan-ops architecture/code-structure.md#child-entity); writes go through NetworkRepository.
public class FirewallRuleRepository(TitanDbContext db) : IFirewallRuleRepository
{
    // A Deleted row is kept (titan-ops features/network/design.md#firewall-rule-soft-delete) but not listed. A rule
    // whose delete is waiting is still listed: Incus still enforces it.
    public async Task<List<FirewallRule>> ListByNetworkAsync(Guid networkId, CancellationToken ct = default) =>
        await db.FirewallRules
            .Where(r => r.NetworkId == networkId && r.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<FirewallRule>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.FirewallRules
            .Where(r => r.ExternalId != null
                     && r.CurrentExistence != CurrentExistence.Deleted
                     && db.Networks.Any(n => n.Id == r.NetworkId
                                          && n.ClusterId == clusterId
                                          && n.DesiredExistence == DesiredExistence.Exists))
            .AsNoTracking()
            .ToListAsync(ct);
}

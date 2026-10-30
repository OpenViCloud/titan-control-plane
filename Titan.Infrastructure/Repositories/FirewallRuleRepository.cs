using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.FirewallRule;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class FirewallRuleRepository(TitanDbContext db) : IFirewallRuleRepository
{
    public async Task<FirewallRule?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
        await db.FirewallRules.FirstOrDefaultAsync(r => r.Id == id, ct);

    // A Deleted row is kept (titan-ops features/network/design.md#firewall-rule-soft-delete) but not listed. A rule
    // whose delete is waiting is still listed: Incus still enforces it.
    public async Task<List<FirewallRule>> ListByNetworkAsync(Guid networkId, CancellationToken ct = default) =>
        await db.FirewallRules
            .Where(r => r.NetworkId == networkId && r.CurrentExistence != CurrentExistence.Deleted)
            .AsNoTracking()
            .ToListAsync(ct);

    // Counted by occupancy (titan-ops features/network/design.md#firewall-rule-status): what Incus holds or is about to
    // hold. A delete asked for stops counting at once; a failed create with nothing on Incus does not count.
    public async Task<int> CountByNetworkAsync(Guid networkId, CancellationToken ct = default) =>
        await db.FirewallRules.CountAsync(
            r => r.NetworkId == networkId
              && r.DesiredExistence == DesiredExistence.Exists
              && (r.CurrentExistence == CurrentExistence.Exists
                  || (r.CurrentExistence == CurrentExistence.NotCreated && r.PendingIntentId != null)), ct);

    public async Task<List<FirewallRule>> ListReconcilableAsync(string clusterId, CancellationToken ct = default) =>
        await db.FirewallRules
            .Where(r => r.ExternalId != null
                     && r.CurrentExistence != CurrentExistence.Deleted
                     && db.Networks.Any(n => n.Id == r.NetworkId
                                          && n.ClusterId == clusterId
                                          && n.DesiredExistence == DesiredExistence.Exists))
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<List<FirewallRule>> ListLiveOfNetworkForUpdateAsync(Guid networkId, CancellationToken ct = default) =>
        await db.FirewallRules
            .Where(r => r.NetworkId == networkId && r.CurrentExistence != CurrentExistence.Deleted)
            .ToListAsync(ct);

    public void Add(FirewallRule rule) => db.FirewallRules.Add(rule);

    public async Task SaveAsync(CancellationToken ct = default) => await db.SaveChangesAsync(ct);
}

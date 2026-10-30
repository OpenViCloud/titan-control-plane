using Titan.Application.Abstractions;
using Titan.Application.Services.Networking.Network;
using Titan.Application.Queries.Networking.FirewallRule.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Networking.FirewallRule;

public class ListFirewallRulesQueryHandler(
    IFirewallRuleRepository repo,
    INetworkRepository networkRepo,
    ICurrentUser currentUser)
{
    public async Task<List<FirewallRuleQueryResult>> HandleAsync(ListFirewallRulesQuery query, CancellationToken ct = default)
    {
        var networkId = query.NetworkId;

        await NetworkOwnership.RequireNetworkAsync(networkRepo, currentUser.RequireTenantId(), networkId, ct);
        var rules = await repo.ListByNetworkAsync(networkId, ct);
        return rules.Select(FirewallRuleQueryResult.From).ToList();
    }
}

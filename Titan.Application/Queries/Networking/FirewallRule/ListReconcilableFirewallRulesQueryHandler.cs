using Titan.Application.Queries.Networking.FirewallRule.Contracts;
using Titan.Domain.Repositories.Networking;

namespace Titan.Application.Queries.Networking.FirewallRule;

// Read side of the firewall reconciler (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#desired-and-current). No
// ICurrentUser: the reconciler acts for no tenant; the endpoint authenticates it by X-Internal-Token.
public class ListReconcilableFirewallRulesQueryHandler(IFirewallRuleRepository rules)
{
    public async Task<List<ReconcilableFirewallRuleQueryResult>> HandleAsync(ListReconcilableFirewallRulesQuery query, CancellationToken ct = default)
    {
        var rows = await rules.ListReconcilableAsync(query.ClusterId, ct);
        return rows.Select(ReconcilableFirewallRuleQueryResult.From).ToList();
    }
}

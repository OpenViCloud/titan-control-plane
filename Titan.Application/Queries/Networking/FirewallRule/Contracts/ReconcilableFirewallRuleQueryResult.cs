using Titan.Domain.Aggregates.FirewallRule;
using FirewallRuleAggregate = Titan.Domain.Aggregates.FirewallRule.FirewallRule;

namespace Titan.Application.Queries.Networking.FirewallRule.Contracts;

// What the reconciler needs to match what Incus holds to a Titan rule and to report only what changed. NetworkId is
// for the callback route. Internal only — never on a customer path.
public record ReconcilableFirewallRuleQueryResult(Guid Id, Guid NetworkId, string ExternalId, string? CurrentState, string CurrentExistence)
{
    public static ReconcilableFirewallRuleQueryResult From(FirewallRuleAggregate r) =>
        new(r.Id, r.NetworkId, r.RequireExternalId(), StateName(r.CurrentState), r.CurrentExistence.ToString());

    private static string? StateName(FirewallRuleState? state)
    {
        if (state == null)
        {
            return null;
        }
        return state.Value.ToString();
    }
}

using Titan.Domain.Aggregates.Network;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;

namespace Titan.Application.Queries.Networking.FirewallRule.Contracts;

// What the reconciler needs to match what Incus holds to a Titan rule and to report only what changed. NetworkId is
// for the callback route. Internal only — never on a customer path. The worker contract keeps its two fields: CurrentState
// is the Incus state the rule holds (InstanceStatus), CurrentExistence is read off the status and that one.
public record ReconcilableFirewallRuleQueryResult(Guid Id, Guid NetworkId, string ExternalId, string? CurrentState, string CurrentExistence)
{
    public static ReconcilableFirewallRuleQueryResult From(FirewallRuleEntity r) =>
        new(r.Id, r.NetworkId, r.GetExternalId(), StateName(r.InstanceStatus), ExistenceName(r));

    private static string ExistenceName(FirewallRuleEntity r)
    {
        if (r.Status == FirewallRuleStatus.NotFound)
        {
            return "NotFound";
        }
        if (r.InstanceStatus != null)
        {
            return "Exists";
        }
        return "NotCreated";
    }

    private static string? StateName(FirewallRuleStatus? state)
    {
        if (state == null)
        {
            return null;
        }
        return state.Value.ToString();
    }
}

using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Commands.Networking.FirewallRule;

// The reconciler's observation of one firewall rule (titan-ops architecture/resource-lifecycle.md#callback-routes): what
// the network's ACL has; no request ends here. Authenticated by X-Internal-Token at the endpoint. Locked through the
// network like every lifecycle handler. A rule the network does not have answers 404 from the network itself
// (Network.GetFirewallRule).
public class ObserveFirewallRuleCommandHandler(INetworkRepository networkRepo)
{
    public async Task HandleAsync(ObserveFirewallRuleCommand command, CancellationToken ct = default)
    {
        // Parsed before the rule is loaded: a malformed report is the worker's bug, whatever the rule.
        FirewallRuleStatus? state = null;
        if (command.Exists)
        {
            state = NetworkReports.ParseState(command.State);
        }

        NetworkAggregate network = await networkRepo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
        if (state != null)
        {
            network.MarkFirewallRuleObserved(command.RuleId, state.Value);
        }
        else
        {
            network.MarkFirewallRuleNotFound(command.RuleId);
        }
        await networkRepo.SaveAsync(ct);
    }
}

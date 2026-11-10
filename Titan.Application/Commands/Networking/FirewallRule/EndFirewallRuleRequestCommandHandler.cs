using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using Titan.Application.Services;

namespace Titan.Application.Commands.Networking.FirewallRule;

// The worker's report that a firewall rule request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes). Authenticated by X-Internal-Token at the endpoint, acting
// for no tenant. The rule is reached through the network the route names, with the network's row locked, the same lock
// a request on the rule takes (titan-ops architecture/code-structure.md#child-entity). A report the rule is not waiting
// on is ignored and still answers 204.
public class EndFirewallRuleRequestCommandHandler(INetworkRepository networkRepo, IActivityRecorder activity)
{
    public async Task HandleAsync(EndFirewallRuleRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the rule is loaded: a malformed report is the worker's bug, whatever the rule.
        OperationOutcome outcome = OperationOutcomes.Parse(command.Outcome);
        FirewallRuleStatus? state = null;
        if (command.State != null)
        {
            state = NetworkReports.ParseState(command.State);
        }

        NetworkAggregate network = await networkRepo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
        FirewallRuleEntity rule = NetworkOwnership.RequireFirewallRule(network, command.RuleId);

        bool ended = NetworkReports.EndRuleOperation(network, rule, outcome, command.RequestId, command.ExternalId, state,
            command.Reason, command.Detail, activity);
        if (!ended)
        {
            return;
        }

        await networkRepo.SaveAsync(ct);
    }
}

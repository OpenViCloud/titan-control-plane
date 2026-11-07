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
        OperationOutcome outcome = NetworkReports.ParseOutcome(command.Outcome);
        FirewallRuleState? state = null;
        if (command.State != null)
        {
            state = NetworkReports.ParseState(command.State);
        }

        NetworkAggregate network = await networkRepo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
        FirewallRuleEntity rule = NetworkOwnership.RequireFirewallRule(network, command.RuleId);

        // The operation the rule waits on says which report this is.
        FirewallRuleOperationKind? waiting = rule.Operation.Kind;
        bool ended;
        switch (waiting)
        {
            case FirewallRuleOperationKind.Create:
                ended = outcome == OperationOutcome.Done
                    ? network.FirewallRuleCreateSucceeded(rule.Id, command.RequestId, command.ExternalId, state)
                    : network.FirewallRuleCreateFailed(rule.Id, command.RequestId, command.ExternalId, state,
                        command.Reason, command.Detail);
                break;
            case FirewallRuleOperationKind.Delete:
                ended = outcome == OperationOutcome.Done
                    ? network.FirewallRuleDeleteSucceeded(rule.Id, command.RequestId)
                    : network.FirewallRuleDeleteFailed(rule.Id, command.RequestId, state, command.Reason, command.Detail);
                break;
            default:
                ended = false;
                break;
        }
        if (!ended)
        {
            return;
        }

        NetworkReports.RecordRuleEnded(rule, waiting!.Value, outcome, activity);
        await networkRepo.SaveAsync(ct);
    }
}

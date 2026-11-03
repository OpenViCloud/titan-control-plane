using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

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
        RequestOutcome outcome = NetworkReports.ParseOutcome(command.Outcome);
        FirewallRuleState? state = null;
        if (command.State != null)
        {
            state = NetworkReports.ParseState(command.State);
        }

        NetworkAggregate network = await networkRepo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
        FirewallRuleEntity rule = NetworkOwnership.RequireFirewallRule(network, command.RuleId);

        // A create that ends Done must name the rule, or it would be left with nothing to delete through.
        bool endsTheCreateWithoutAnId = outcome == RequestOutcome.Done
                                        && rule.Pending is { Kind: FirewallRuleRequestKind.Create } waiting
                                        && waiting.Id == command.RequestId
                                        && rule.ExternalId == null
                                        && string.IsNullOrEmpty(command.ExternalId);
        if (endsTheCreateWithoutAnId)
        {
            throw new BadRequestException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }

        FirewallRuleRequestEnd end = new(outcome, command.ExternalId, state, command.Reason, command.Detail);
        PendingRequest<FirewallRuleRequestKind>? ended = network.EndFirewallRuleRequest(rule.Id, command.RequestId, end);
        if (ended == null)
        {
            return;
        }

        NetworkReports.RecordRuleEnded(rule, ended, outcome, activity);
        await networkRepo.SaveAsync(ct);
    }
}

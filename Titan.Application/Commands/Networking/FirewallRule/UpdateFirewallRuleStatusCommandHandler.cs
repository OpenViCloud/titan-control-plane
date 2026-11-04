using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using Titan.Application.Exceptions;
using Titan.Application.Services;

namespace Titan.Application.Commands.Networking.FirewallRule;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): each
// report is mapped onto the request the rule is waiting on. Authenticated by X-Internal-Token at the endpoint, so no
// ownership check here. The rule is reached through the network the route names, with the network's row locked
// (titan-ops architecture/code-structure.md#child-entity).
public class UpdateFirewallRuleStatusCommandHandler(INetworkRepository networkRepo, IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateFirewallRuleStatusCommand command, CancellationToken ct = default)
    {
        // Parsed before the rule is loaded: a malformed report is the worker's bug, whatever the rule.
        FirewallRuleState? observedState = null;
        switch (command.Report)
        {
            case "Observed":
                if (command.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                if (command.Exists == true)
                {
                    observedState = NetworkReports.ParseState(command.State);
                }
                break;
            case "Created":
                if (string.IsNullOrEmpty(command.ExternalRuleId))
                {
                    throw new BadRequestException("INVALID_STATUS", "ExternalRuleId is required when report is Created.");
                }
                break;
            case "RequestFailed":
            case "Deleted":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown firewall rule report: '{command.Report}'");
        }

        NetworkAggregate network = await networkRepo.FindByIdForUpdateAsync(command.NetworkId, ct)
            ?? throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
        FirewallRuleEntity rule = NetworkOwnership.RequireFirewallRule(network, command.RuleId);

        PendingOperation<FirewallRuleOperationKind>? waiting = rule.Pending;
        PendingOperation<FirewallRuleOperationKind>? ended = null;
        OperationOutcome outcome = OperationOutcome.Done;
        switch (command.Report)
        {
            case "Observed":
                if (observedState == null)
                {
                    network.MarkFirewallRuleNotFound(rule.Id);
                }
                else
                {
                    network.MarkFirewallRuleObserved(rule.Id, observedState.Value);
                }
                break;
            case "Created":
                // The old create command reports the rule's id as soon as it is in the ACL: the create's Done.
                if (waiting is { Kind: FirewallRuleOperationKind.Create })
                {
                    ended = network.FinishFirewallRuleOperation(rule.Id, waiting.Id, command.ExternalRuleId, null);
                }
                break;
            case "RequestFailed":
                // The waiting request ends Failed, keeping an id the create's give-up found.
                if (waiting != null)
                {
                    outcome = OperationOutcome.Failed;
                    ended = network.FailFirewallRuleOperation(rule.Id, waiting.Id, command.ExternalRuleId, null, command.Reason,
                        command.Detail);
                }
                break;
            case "Deleted":
                if (waiting is { Kind: FirewallRuleOperationKind.Delete })
                {
                    ended = network.FinishFirewallRuleOperation(rule.Id, waiting.Id, null, null);
                }
                else
                {
                    network.MarkFirewallRuleNotFound(rule.Id);
                }
                break;
        }

        if (ended != null)
        {
            NetworkReports.RecordRuleEnded(rule, ended, outcome, activity);
        }

        await networkRepo.SaveAsync(ct);
    }
}

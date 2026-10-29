using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Domain.Aggregates.Networking.FirewallRule;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Networking;
using FirewallRuleAggregate = Titan.Domain.Aggregates.Networking.FirewallRule.FirewallRule;

namespace Titan.Application.Commands.Networking.FirewallRule;

// Worker callback — authenticated by X-Internal-Token at the endpoint, so no ownership check here. The create and
// delete commands and the firewall reconciler all report here. Outcomes are recorded once per request: the aggregate
// says whether a report fulfilled the waiting one (titan-ops features/activity/design.md#outcome-source).
public class UpdateFirewallRuleStatusCommandHandler(IFirewallRuleRepository repo, IActivityRecorder activity)
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
                    observedState = ParseIncusState(command.State);
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

        FirewallRuleAggregate? rule = await repo.FindByIdAsync(command.RuleId, ct);
        if (rule == null)
        {
            throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
        }

        string? outcome = null;
        switch (command.Report)
        {
            case "Observed":
                if (observedState == null)
                {
                    rule.MarkNotFound();
                }
                else
                {
                    bool created = rule.MarkObserved(observedState.Value);
                    if (created)
                    {
                        outcome = "firewall_rule.created";
                    }
                }
                break;
            case "Created":
                rule.MarkCreated(command.ExternalRuleId!);
                break;
            case "RequestFailed":
                // A give-up report carries the provider id when the Created report never arrived.
                bool carriesAnId = !string.IsNullOrEmpty(command.ExternalRuleId);
                if (carriesAnId)
                {
                    rule.MarkCreated(command.ExternalRuleId!);
                }
                // A failed delete gives the rule back — it is still in the ACL and still enforced
                // (titan-ops architecture/desired-and-current.md#abandoned-request).
                bool deleteWasWaiting = rule.Transition == FirewallRuleTransition.Deleting;
                if (deleteWasWaiting)
                {
                    rule.AbandonRequest(command.Reason, command.Detail);
                }
                else
                {
                    rule.MarkRequestFailed(command.Reason, command.Detail);
                }
                break;
            case "Deleted":
                bool deleted = rule.MarkDeleted();
                if (deleted)
                {
                    outcome = "firewall_rule.deleted";
                }
                break;
        }

        if (outcome != null)
        {
            activity.StageOutcome(rule.TenantId, outcome, "firewall_rule", rule.Id, FirewallRuleLabel.Describe(rule));
        }

        await repo.SaveAsync(ct);
    }

    // The worker sends the Incus ACL rule state word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    // Matched by name, exactly: Enum.TryParse would also take "1" or "Enabled".
    private static FirewallRuleState ParseIncusState(string? state)
    {
        foreach (FirewallRuleState known in Enum.GetValues<FirewallRuleState>())
        {
            if (known.ToString() == state)
            {
                return known;
            }
        }
        throw new BadRequestException("INVALID_STATUS", $"Unknown firewall rule state: '{state}'");
    }
}

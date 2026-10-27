using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Networking.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Networking.Network.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Networking;
using FirewallRuleAggregate = Titan.Domain.Aggregates.Networking.FirewallRule.FirewallRule;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.FirewallRule;

public class DeleteFirewallRuleCommandHandler(
    IFirewallRuleRepository repo,
    INetworkRepository networkRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteFirewallRuleCommand command, CancellationToken ct = default)
    {
        Guid networkId = command.NetworkId;
        Guid ruleId = command.RuleId;

        NetworkAggregate network = await NetworkOwnership.RequireNetworkAsync(networkRepo, currentUser.RequireTenantId(), networkId, ct);

        FirewallRuleAggregate? rule = await repo.FindByIdAsync(ruleId, ct);
        if (rule == null || rule.NetworkId != networkId)
        {
            throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
        }

        // The aggregate refuses a repeat, so the handler recognises it first
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This delete was already requested, or the rule is already gone.
        // Answer like the first request: nothing recorded, nothing sent.
        bool alreadyRequestedOrDone = rule.Transition == FirewallRuleTransition.Deleting || rule.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        string label = FirewallRuleLabel.Describe(rule);

        // The create failed, so the provider never had this rule: there is nothing to send and
        // nothing to wait for. The request and its outcome happen here at once (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-has-identity).
        bool neverReachedTheProvider = rule.ExternalId == null && rule.Transition == null;
        if (neverReachedTheProvider)
        {
            rule.DeleteLocally();
            activity.Stage("firewall_rule.delete_requested", "firewall_rule", rule.Id, label);
            activity.StageOutcome(rule.TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id, label);
            await repo.SaveAsync(ct);
            return;
        }

        // The rule stays (delete waiting) until the worker reports the provider has removed it
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#forget-on-provider-confirm): forgetting it here would leave a rule
        // enforced that Titan no longer lists.
        string externalRuleId = rule.RequestDelete();
        activity.Stage("firewall_rule.delete_requested", "firewall_rule", rule.Id, label);
        await commands.EnqueueFirewallRuleDeleteAsync(
            rule.Id, networkId, externalRuleId, TenantAggregate.ProjectNameFor(network.TenantId), network.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}

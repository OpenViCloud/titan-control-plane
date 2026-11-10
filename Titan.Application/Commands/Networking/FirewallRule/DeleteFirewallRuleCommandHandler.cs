using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.FirewallRule;

public class DeleteFirewallRuleCommandHandler(
    INetworkRepository networkRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteFirewallRuleCommand command, CancellationToken ct = default)
    {
        Guid networkId = command.NetworkId;
        Guid ruleId = command.RuleId;

        // Locked, so two deletes of the rule take turns and the second sees the first waiting
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        NetworkAggregate network = await NetworkOwnership.RequireNetworkForUpdateAsync(networkRepo, currentUser.RequireTenantId(), networkId, ct);

        // A rule of another network answers like a missing one (titan-ops features/network/design.md#firewall-rule-soft-delete).
        FirewallRuleEntity rule = NetworkOwnership.RequireFirewallRule(network, ruleId);

        // A repeat is answered like the first request: nothing recorded, nothing sent
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A delete that failed is not a
        // repeat: it falls through and the aggregate decides.
        if (rule.IsDeletedOrDeleting)
        {
            return;
        }

        string label = FirewallRuleLabel.Describe(rule);

        // The rule stays (delete waiting) until the worker reports the provider has removed it
        // (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm): forgetting it here would leave a rule
        // enforced that Titan no longer lists.
        // A rule that never reached the provider is deleted here and gets no ticket: nothing to send and nothing to
        // wait for, so the request and its outcome happen at once (titan-ops architecture/intent-and-observation.md#intent-has-identity).
        OperationTicket? ticket = network.DeleteFirewallRule(ruleId);
        activity.Stage("firewall_rule.delete_requested", "firewall_rule", rule.Id, label);
        if (ticket == null)
        {
            activity.StageOutcome(rule.TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id, label);
            await networkRepo.SaveAsync(ct);
            return;
        }
        await commands.EnqueueFirewallRuleDeleteAsync(network, rule, ticket.OperationId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(network.TenantId), ct);
        await networkRepo.SaveAsync(ct);
    }
}

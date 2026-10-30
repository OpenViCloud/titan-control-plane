using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.FirewallRule;

public class CreateFirewallRuleCommandHandler(
    INetworkRepository networkRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<CreateFirewallRuleCommandResult> HandleAsync(CreateFirewallRuleCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the wire contract (titan-ops architecture/code-structure.md#command-is-wire-contract)
        var networkId = command.NetworkId;

        // Locked: the per-network cap is counted on the rules loaded with the network, so a second request adding a
        // rule at the cap waits here and then counts this one (titan-ops architecture/code-structure.md#child-entity).
        NetworkAggregate network = await NetworkOwnership.RequireNetworkForUpdateAsync(networkRepo, currentUser.RequireTenantId(), networkId, ct);

        // Cap is per network and owned by the network's tenant (titan-ops architecture/tenancy-and-admin.md#tenant-quota).
        var tenant = await tenantRepo.FindByIdAsync(network.TenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        var direction = Enum.Parse<FirewallDirection>(req.Direction, ignoreCase: true);
        var protocol = Enum.Parse<FirewallProtocol>(req.Protocol, ignoreCase: true);

        // The network answers whether it can take a rule, checks the cap and adds the rule in one call; the provider
        // id is read after, from a network that just said it is usable.
        FirewallRuleEntity rule = network.AddFirewallRule(tenant, direction, protocol, req.StartPort, req.EndPort, req.Cidr);
        string externalNetworkId = network.RequireExternalId();

        activity.Stage("firewall_rule.create_requested", "firewall_rule", rule.Id, FirewallRuleLabel.Describe(rule)); // atomic with the save
        await commands.EnqueueFirewallRuleCreateAsync(
            rule.Id, networkId, req.Direction, req.Protocol,
            req.StartPort, req.EndPort, req.Cidr,
            externalNetworkId, TenantAggregate.ProjectNameFor(network.TenantId), network.ClusterId, ct);
        await networkRepo.SaveAsync(ct);

        return new CreateFirewallRuleCommandResult(rule.Id);
    }
}

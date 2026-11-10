using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Tenant;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
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
        var networkId = command.NetworkId;

        // Locked: the per-network cap is counted on the rules loaded with the network, so a second request adding a
        // rule at the cap waits here and then counts this one (titan-ops architecture/code-structure.md#child-entity).
        NetworkAggregate network = await NetworkOwnership.RequireNetworkForUpdateAsync(networkRepo, currentUser.RequireTenantId(), networkId, ct);

        // Cap is per network and owned by the network's tenant (titan-ops architecture/tenancy-and-admin.md#tenant-quota).
        var tenant = await tenantRepo.FindByIdAsync(network.TenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        var direction = Enum.Parse<FirewallDirection>(command.Direction, ignoreCase: true);
        var protocol = Enum.Parse<FirewallProtocol>(command.Protocol, ignoreCase: true);
        // The validator already paired the ports with the protocol and answered 400 for a bad shape; these are the
        // Domain's own words for the same rules.
        PortRange? ports = null;
        if (command.StartPort != null && command.EndPort != null)
        {
            ports = PortRange.Of(command.StartPort.Value, command.EndPort.Value);
        }
        FirewallCidr cidr = FirewallCidr.Parse(command.Cidr);

        TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.FirewallRule, network.FirewallRuleQuotaUsage);

        // The network answers whether it can take a rule and adds it in one call.
        Guid ruleId = Guid.NewGuid();
        OperationTicket ticket = network.AddFirewallRule(ruleId, direction, protocol, ports, cidr);
        FirewallRuleEntity rule = NetworkOwnership.RequireFirewallRule(network, ruleId);

        activity.Stage("firewall_rule.create_requested", "firewall_rule", ruleId, FirewallRuleLabel.Describe(rule)); // atomic with the save
        await commands.EnqueueFirewallRuleCreateAsync(network, rule, ticket.OperationId,
            TenantAggregate.ProjectNameFor(network.TenantId), ct);
        await networkRepo.SaveAsync(ct);

        return new CreateFirewallRuleCommandResult(ruleId);
    }
}

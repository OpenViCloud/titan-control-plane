using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.FirewallRule;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FirewallRuleAggregate = Titan.Domain.Aggregates.FirewallRule.FirewallRule;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Networking.FirewallRule;

public class CreateFirewallRuleCommandHandler(
    IFirewallRuleRepository repo,
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

        var network = await NetworkOwnership.RequireNetworkAsync(networkRepo, currentUser.RequireTenantId(), networkId, ct);

        // The network answers whether it can take a rule AND hands over the provider id in one
        // call: the precondition cannot be skipped, and there is no `!` below.
        var externalNetworkId = network.BeginFirewallRule();

        // Cap is per network and owned by the network's tenant (titan-ops architecture/tenancy-and-admin.md#tenant-quota).
        var tenant = await tenantRepo.FindByIdAsync(network.TenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");
        var ruleCount = await repo.CountByNetworkAsync(networkId, ct);

        var direction = Enum.Parse<FirewallDirection>(req.Direction, ignoreCase: true);
        var protocol = Enum.Parse<FirewallProtocol>(req.Protocol, ignoreCase: true);

        var rule = FirewallRuleAggregate.Create(networkId, tenant, ruleCount, direction, protocol, req.StartPort, req.EndPort, req.Cidr);
        repo.Add(rule);
        activity.Stage("firewall_rule.create_requested", "firewall_rule", rule.Id, FirewallRuleLabel.Describe(rule)); // atomic with Add
        await commands.EnqueueFirewallRuleCreateAsync(
            rule.Id, networkId, req.Direction, req.Protocol,
            req.StartPort, req.EndPort, req.Cidr,
            externalNetworkId, TenantAggregate.ProjectNameFor(network.TenantId), network.ClusterId, ct);
        await repo.SaveAsync(ct);

        return new CreateFirewallRuleCommandResult(rule.Id);
    }
}

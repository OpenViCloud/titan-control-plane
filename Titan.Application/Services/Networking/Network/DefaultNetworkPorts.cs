using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Services.Networking.FirewallRule;
using Titan.Application.Services.Platform.Tenant;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Services.Networking.Network;

/// The Ingress TCP rules the default network opens from anywhere (titan-ops features/network/design.md#default-network):
/// every port when its create ends, and the web ports on a default network made before it opened them.
public static class DefaultNetworkPorts
{
    public static readonly IReadOnlyList<int> WhenCreated = [22, 80, 443];
    public static readonly IReadOnlyList<int> AddedLater = [80, 443];

    private static readonly IReadOnlyList<string> FromAnywhere = ["::/0", "0.0.0.0/0"];

    /// Adds the rules of these ports the network lacks, all or none: a refusal (the network not Created, no room for
    /// every one of them) logs and adds nothing. Never saves; returns how many it added.
    public static async Task<int> OpenAsync(NetworkAggregate network, TenantAggregate tenant, IReadOnlyList<int> ports,
        IProviderCommandQueue commands, IActivityRecorder activity, ILogger logger, CancellationToken ct)
    {
        List<(int Port, FirewallCidr Cidr)> missing = ports
            .SelectMany(port => FromAnywhere.Select(cidr => (Port: port, Cidr: FirewallCidr.Parse(cidr))))
            .Where(wanted => !network.FirewallRules.Any(r => !r.IsDeletedOrDeleting && IsOpening(r, wanted.Port, wanted.Cidr)))
            .ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        try
        {
            // Room for the last rule too, before the first is added.
            TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.FirewallRule, network.FirewallRuleQuotaUsage + missing.Count - 1);
            foreach ((int port, FirewallCidr cidr) in missing)
            {
                Guid ruleId = Guid.NewGuid();
                OperationTicket ticket = network.AddFirewallRule(ruleId, FirewallDirection.Ingress, FirewallProtocol.TCP,
                    PortRange.Of(port, port), cidr);
                FirewallRuleEntity rule = NetworkOwnership.RequireFirewallRule(network, ruleId);
                activity.StageOutcome(tenant.Id, "firewall_rule.create_requested", "firewall_rule", ruleId,
                    FirewallRuleLabel.Describe(rule));
                await commands.EnqueueFirewallRuleCreateAsync(network, rule, ticket.OperationId,
                    TenantAggregate.ProjectNameFor(tenant.Id), ct);
            }
        }
        catch (BusinessException e)
        {
            logger.LogWarning("Default network {NetworkId}: {Code}; its {Count} rules not added.", network.Id, e.Code,
                missing.Count);
            return 0;
        }
        return missing.Count;
    }

    private static bool IsOpening(FirewallRuleEntity rule, int port, FirewallCidr cidr) =>
        rule.Direction == FirewallDirection.Ingress && rule.Protocol == FirewallProtocol.TCP
        && rule.StartPort == port && rule.EndPort == port && rule.Cidr.Value == cidr.Value;
}

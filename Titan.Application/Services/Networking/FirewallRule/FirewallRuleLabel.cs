using Titan.Domain.Aggregates.Network;
using FirewallRuleEntity = Titan.Domain.Aggregates.Network.FirewallRule;

namespace Titan.Application.Services.Networking.FirewallRule;

/// A firewall rule has no name, so the activity feed describes it in the words of the rule itself:
/// "Ingress TCP 22 from 0.0.0.0/0". Shared by the create/delete handlers and the worker callbacks,
/// which must name the rule the same way for a request and its outcome to read as a pair.
public static class FirewallRuleLabel
{
    public static string Describe(FirewallRuleEntity rule)
    {
        string ports = DescribePorts(rule.StartPort, rule.EndPort);
        string preposition = "from";
        if (rule.Direction == FirewallDirection.Egress)
        {
            preposition = "to";
        }

        string protocolAndPorts = rule.Protocol.ToString();
        if (ports.Length > 0)
        {
            protocolAndPorts = $"{rule.Protocol} {ports}";
        }

        return $"{rule.Direction} {protocolAndPorts} {preposition} {rule.Cidr.Value}";
    }

    private static string DescribePorts(int? startPort, int? endPort)
    {
        if (startPort == null)
        {
            return "";
        }

        bool singlePort = endPort == null || endPort == startPort;
        if (singlePort)
        {
            return $"{startPort}";
        }

        return $"{startPort}-{endPort}";
    }
}

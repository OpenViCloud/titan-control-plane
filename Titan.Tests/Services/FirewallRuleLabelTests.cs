using Titan.Application.Services.Networking.FirewallRule;
using Titan.Domain.Aggregates.Network;

namespace Titan.Tests.Services;

// A firewall rule has no name, so the activity feed needs words for it.
public class FirewallRuleLabelTests
{
    [Theory]
    [InlineData(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0", "Ingress TCP 22 from 0.0.0.0/0")]
    [InlineData(FirewallDirection.Ingress, FirewallProtocol.UDP, 8000, 8100, "10.0.0.0/8", "Ingress UDP 8000-8100 from 10.0.0.0/8")]
    [InlineData(FirewallDirection.Egress, FirewallProtocol.TCP, 443, 443, "0.0.0.0/0", "Egress TCP 443 to 0.0.0.0/0")]
    [InlineData(FirewallDirection.Egress, FirewallProtocol.ICMP, null, null, "10.0.0.0/8", "Egress ICMP to 10.0.0.0/8")]
    [InlineData(FirewallDirection.Ingress, FirewallProtocol.All, null, null, "192.168.0.0/16", "Ingress All from 192.168.0.0/16")]
    public void Describe_ReadsLikeTheRule(FirewallDirection direction, FirewallProtocol protocol, int? startPort, int? endPort, string cidr, string expected)
    {
        FirewallRule rule = FirewallRule.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), direction, protocol, startPort, endPort, cidr);

        Assert.Equal(expected, FirewallRuleLabel.Describe(rule));
    }
}

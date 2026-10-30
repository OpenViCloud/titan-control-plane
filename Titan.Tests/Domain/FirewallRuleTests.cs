using Titan.Domain.Aggregates.FirewallRule;
using Titan.Domain.Aggregates.Network;

namespace Titan.Tests.Domain;

public class FirewallRuleTests
{
    private static readonly Guid NetworkId = Guid.NewGuid();
    private static readonly Guid TenantId = Guid.NewGuid();

    [Fact]
    public void Create_FillsAllFields()
    {
        var rule = FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 80, 80, "0.0.0.0/0");

        Assert.NotEqual(Guid.Empty, rule.Id);
        Assert.Equal(NetworkId, rule.NetworkId);
        Assert.Equal(TenantId, rule.TenantId);
        Assert.Equal(FirewallDirection.Ingress, rule.Direction);
        Assert.Equal(FirewallProtocol.TCP, rule.Protocol);
        Assert.Equal(80, rule.StartPort);
        Assert.Equal(80, rule.EndPort);
        Assert.Equal("0.0.0.0/0", rule.Cidr);
        Assert.Null(rule.ExternalId);
        Assert.True(rule.CreatedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public void Create_IcmpRule_HasNullPorts()
    {
        var rule = FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Ingress, FirewallProtocol.ICMP, null, null, "10.0.0.0/8");

        Assert.Null(rule.StartPort);
        Assert.Null(rule.EndPort);
    }

    [Fact]
    public void MarkActive_SetsExternalId()
    {
        var rule = FirewallRule.CreateUnchecked(NetworkId, TenantId, FirewallDirection.Egress, FirewallProtocol.UDP, 53, 53, "0.0.0.0/0");

        rule.MarkActive("cs-rule-abc");

        Assert.Equal("cs-rule-abc", rule.ExternalId);
    }
}

public class NetworkFirewallPolicyTests
{
    [Fact]
    public void Create_DefaultPoliciesAreAllow()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "net-1", "cluster-1");

        Assert.Equal(FirewallPolicy.Allow, network.DefaultIngressPolicy);
        Assert.Equal(FirewallPolicy.Allow, network.DefaultEgressPolicy);
    }

    [Fact]
    public void Create_WithDenyEgressPolicy_SetsEgressPolicy()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "net-1", "cluster-1",
            defaultIngressPolicy: FirewallPolicy.Deny,
            defaultEgressPolicy: FirewallPolicy.Deny);

        Assert.Equal(FirewallPolicy.Deny, network.DefaultIngressPolicy);
        Assert.Equal(FirewallPolicy.Deny, network.DefaultEgressPolicy);
    }

    [Fact]
    public void Create_MixedPolicies_SetsIndependently()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "net-1", "cluster-1",
            defaultIngressPolicy: FirewallPolicy.Deny,
            defaultEgressPolicy: FirewallPolicy.Allow);

        Assert.Equal(FirewallPolicy.Deny, network.DefaultIngressPolicy);
        Assert.Equal(FirewallPolicy.Allow, network.DefaultEgressPolicy);
    }
}

using FluentValidation.TestHelper;
using Titan.Infrastructure.Endpoints.Network;

namespace Titan.Tests.Infrastructure;

// A rule's CIDR may be IPv4 or IPv6: a network with public IPv6 needs rules for it, and ICMP follows the CIDR's
// address family on the worker (titan-ops features/network/design.md#firewall-on-incus).
public class CreateFirewallRuleValidatorTests
{
    private readonly CreateFirewallRuleValidator _validator = new();

    private static CreateFirewallRuleRequest Tcp22(string cidr) => new("Ingress", "TCP", 22, 22, cidr);

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/8")]
    [InlineData("::/0")]
    [InlineData("2001:db8::/32")]
    [InlineData("2001:41d0:d00:d470::/64")]
    [InlineData("2001:41d0:d00:d470::5/128")]
    public void Ipv4AndIpv6Cidrs_AreAccepted(string cidr)
    {
        _validator.TestValidate(Tcp22(cidr)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Icmp_OverIpv6_IsAccepted()
    {
        _validator.TestValidate(new CreateFirewallRuleRequest("Ingress", "ICMP", null, null, "::/0")).ShouldNotHaveAnyValidationErrors();
    }

    // ::ffff:a.b.c.d is an IPv4 range in IPv6 form: the worker cannot tell which ICMP it means.
    [Theory]
    [InlineData("::ffff:10.0.0.0/104")]
    [InlineData("2001:db8::/129")]
    [InlineData("2001:db8::1/32")]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.0")]
    public void ACidrNeitherIpv4NorIpv6_IsRejected(string cidr)
    {
        _validator.TestValidate(Tcp22(cidr)).ShouldHaveValidationErrorFor(r => r.Cidr);
    }

    [Theory]
    [InlineData("TCP")]
    [InlineData("UDP")]
    public void TcpOrUdp_WithoutPorts_IsRejected(string protocol)
    {
        var result = _validator.TestValidate(new CreateFirewallRuleRequest("Ingress", protocol, null, null, "0.0.0.0/0"));

        result.ShouldHaveValidationErrorFor(r => r.StartPort).WithErrorMessage("'StartPort' is required for TCP/UDP.");
        result.ShouldHaveValidationErrorFor(r => r.EndPort).WithErrorMessage("'EndPort' is required for TCP/UDP.");
    }

    [Fact]
    public void TcpPortOutOfRange_IsRejected()
    {
        _validator.TestValidate(new CreateFirewallRuleRequest("Ingress", "TCP", 0, 22, "0.0.0.0/0"))
            .ShouldHaveValidationErrorFor(r => r.StartPort).WithErrorMessage("'StartPort' must be between 1 and 65535.");
        _validator.TestValidate(new CreateFirewallRuleRequest("Ingress", "TCP", 22, 65536, "0.0.0.0/0"))
            .ShouldHaveValidationErrorFor(r => r.EndPort).WithErrorMessage("'EndPort' must be between 1 and 65535.");
    }

    [Fact]
    public void TcpStartAfterEnd_IsRejected()
    {
        var result = _validator.TestValidate(new CreateFirewallRuleRequest("Ingress", "TCP", 443, 80, "0.0.0.0/0"));

        Assert.Contains(result.Errors, e => e.ErrorMessage == "'StartPort' must be less than or equal to 'EndPort'.");
    }

    [Theory]
    [InlineData("ICMP")]
    [InlineData("All")]
    public void IcmpOrAll_WithAPort_IsRejected(string protocol)
    {
        _validator.TestValidate(new CreateFirewallRuleRequest("Ingress", protocol, 0, null, "0.0.0.0/0"))
            .ShouldHaveValidationErrorFor(r => r.StartPort).WithErrorMessage("'StartPort' must be null for ICMP/All protocol.");
    }

    [Fact]
    public void All_WithoutPorts_IsAccepted()
    {
        _validator.TestValidate(new CreateFirewallRuleRequest("Egress", "All", null, null, "0.0.0.0/0")).ShouldNotHaveAnyValidationErrors();
    }
}

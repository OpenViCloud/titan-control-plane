using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// The shape rules of a network's name, CIDR and port range live in the Domain, so no path — the API, a handler, a
// test seam — builds a network or rule Incus or the portal cannot handle (titan-ops architecture/code-structure.md#logic-placement).
// The validators translate the same rules to 400 with their own wording (CreateNetworkValidatorTests,
// CreateFirewallRuleValidatorTests).
public class NetworkValueObjectTests
{
    // ---- NetworkName

    [Theory]
    [InlineData("-a")]
    [InlineData("_a")]
    [InlineData("a b")]
    [InlineData("tên")]
    [InlineData("")]
    public void NetworkName_RefusesALeadingHyphen_AForeignCharacter_AndEmpty(string name)
    {
        Assert.Equal("INVALID_NETWORK_NAME", Assert.Throws<BusinessException>(() => NetworkName.Parse(name)).Code);
        Assert.False(NetworkName.TryParse(name, out _));
    }

    [Fact]
    public void NetworkName_RefusesOver100Characters()
    {
        string hundred = new('a', 100);
        Assert.Equal(hundred, NetworkName.Parse(hundred).Value);
        Assert.Equal("INVALID_NETWORK_NAME", Assert.Throws<BusinessException>(() => NetworkName.Parse(hundred + "a")).Code);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("net-1")]
    [InlineData("my_net-01")]
    [InlineData("0abc")]
    public void NetworkName_AcceptsLettersDigitsHyphenUnderscore(string name)
    {
        Assert.Equal(name, NetworkName.Parse(name).Value);
        Assert.True(NetworkName.TryParse(name, out NetworkName? parsed));
        Assert.Equal(name, parsed!.Value);
    }

    // ---- Ipv4Cidr

    [Theory]
    [InlineData("10.0.0.0/33")]
    [InlineData("999.1.1.1/24")]
    [InlineData("10.0.0.300/24")]
    [InlineData("10.0.0.0")]
    [InlineData("::/0")]
    [InlineData("not-a-cidr")]
    [InlineData("")]
    public void Ipv4Cidr_RefusesAPrefixOver32_AnOctetOver255_AndNotACidr(string cidr)
    {
        Assert.Equal("INVALID_CIDR", Assert.Throws<BusinessException>(() => Ipv4Cidr.Parse(cidr)).Code);
        Assert.False(Ipv4Cidr.TryParse(cidr, out _));
    }

    // The address need not be the first of its range: the /v1 contract takes 10.0.0.5/24, and the worker masks it and
    // gives Incus the first host as ipv4.address (titan-ops features/network/design.md#network-worker).
    [Theory]
    [InlineData("10.0.0.5/24")]
    [InlineData("0.0.0.0/0")]
    [InlineData("255.255.255.255/32")]
    [InlineData("10.0.1.0/24")]
    public void Ipv4Cidr_AcceptsAnyAddressWithAPrefixUpTo32(string cidr)
    {
        Assert.Equal(cidr, Ipv4Cidr.Parse(cidr).Value);
        Assert.True(Ipv4Cidr.TryParse(cidr, out _));
    }

    // Same table as CreateNetworkValidatorTests: inside one of the three private ranges, with a prefix at least as
    // long as the range's — a supernet of a private range is not private.
    [Theory]
    [InlineData("10.20.0.0/24", true)]
    [InlineData("172.16.5.0/24", true)]
    [InlineData("192.168.10.0/24", true)]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("8.8.8.0/24", false)]
    [InlineData("172.32.0.0/24", false)]
    [InlineData("100.64.0.0/24", false)]
    [InlineData("9.0.0.0/7", false)]
    public void Ipv4Cidr_IsInsideAPrivateRange_ForTheThreeRanges_NotForTheirSupernets(string cidr, bool inside)
    {
        Assert.Equal(inside, Ipv4Cidr.Parse(cidr).IsInsideAPrivateRange);
    }

    // ---- FirewallCidr

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("10.0.0.0/8")]
    [InlineData("::/0")]
    [InlineData("2001:db8::/32")]
    [InlineData("2001:41d0:d00:d470::/64")]
    [InlineData("2001:41d0:d00:d470::5/128")]
    public void FirewallCidr_AcceptsIpv4AndIpv6(string cidr)
    {
        Assert.Equal(cidr, FirewallCidr.Parse(cidr).Value);
        Assert.True(FirewallCidr.TryParse(cidr, out _));
    }

    // Written as the range's first address; not ::ffff:a.b.c.d, an IPv4 range whose ICMP family the worker would guess.
    [Theory]
    [InlineData("::ffff:10.0.0.0/104")]
    [InlineData("2001:db8::/129")]
    [InlineData("2001:db8::1/32")]
    [InlineData("10.0.0.300/24")]
    [InlineData("10.0.0.0/33")]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.0")]
    public void FirewallCidr_RefusesV4Mapped_AHostBitIpv6_AnOctetOver255_AndNotACidr(string cidr)
    {
        Assert.Equal("INVALID_CIDR", Assert.Throws<BusinessException>(() => FirewallCidr.Parse(cidr)).Code);
        Assert.False(FirewallCidr.TryParse(cidr, out _));
    }

    // ---- PortRange

    [Theory]
    [InlineData(0, 80)]
    [InlineData(80, 65536)]
    [InlineData(80, 79)]
    [InlineData(-1, -1)]
    public void PortRange_RefusesOutside1To65535_AndStartAfterEnd(int start, int end)
    {
        Assert.Equal("INVALID_PORT_RANGE", Assert.Throws<BusinessException>(() => PortRange.Of(start, end)).Code);
        Assert.False(PortRange.TryOf(start, end, out _));
    }

    [Theory]
    [InlineData(22, 22)]
    [InlineData(1, 65535)]
    [InlineData(8000, 8080)]
    public void PortRange_AcceptsBothEndsInclusive(int start, int end)
    {
        PortRange range = PortRange.Of(start, end);
        Assert.Equal(start, range.Start);
        Assert.Equal(end, range.End);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void PortRange_IsPort_IsTheSameBoundAsOf(int port, bool isPort)
    {
        Assert.Equal(isPort, PortRange.IsPort(port));
    }

    // ---- Ipv4Cidr.Overlaps: the range arithmetic behind NetworkRepository.HasCidrOverlapAsync.

    [Theory]
    [InlineData("10.1.0.0/24", "10.1.0.0/25", true)]
    [InlineData("10.1.0.0/25", "10.1.0.0/24", true)]
    [InlineData("10.1.0.0/24", "10.1.0.0/24", true)]
    [InlineData("10.1.0.5/24", "10.1.0.200/24", true)]
    [InlineData("0.0.0.0/0", "192.168.7.0/24", true)]
    [InlineData("10.1.0.0/24", "10.1.1.0/24", false)]
    [InlineData("10.1.0.0/24", "10.2.0.0/24", false)]
    [InlineData("10.1.0.255/32", "10.1.1.0/32", false)]
    public void Ipv4Cidr_Overlaps_WhenTheTwoRangesShareAnAddress(string one, string other, bool overlaps)
    {
        Assert.Equal(overlaps, Ipv4Cidr.Parse(one).Overlaps(Ipv4Cidr.Parse(other)));
    }

    // ---- Value semantics (titan-ops architecture/code-structure.md#domain-base-classes)

    [Fact]
    public void ValueObjects_CompareByValue()
    {
        Assert.Equal(NetworkName.Parse("net-1"), NetworkName.Parse("net-1"));
        Assert.NotEqual(NetworkName.Parse("net-1"), NetworkName.Parse("net-2"));
        Assert.Equal(Ipv4Cidr.Parse("10.0.0.0/24"), Ipv4Cidr.Parse("10.0.0.0/24"));
        Assert.Equal(FirewallCidr.Parse("::/0"), FirewallCidr.Parse("::/0"));
        Assert.Equal(PortRange.Of(80, 80), PortRange.Of(80, 80));
        Assert.NotEqual(PortRange.Of(80, 80), PortRange.Of(80, 81));
    }
}

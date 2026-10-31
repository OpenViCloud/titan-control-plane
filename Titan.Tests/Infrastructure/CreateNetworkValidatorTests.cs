using FluentValidation.TestHelper;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Endpoints.Network;

namespace Titan.Tests.Infrastructure;

// A network with public IPv6 sits on the cluster's uplink, and Incus refuses one whose IPv4 CIDR the uplink does not
// route; the uplink routes the three private ranges (titan-ops infra/incus/design.md#ipv6-address-plan).
public class CreateNetworkValidatorTests
{
    private readonly CreateNetworkValidator _validator = new();

    [Theory]
    [InlineData("10.20.0.0/24")]
    [InlineData("172.16.5.0/24")]
    [InlineData("192.168.10.0/24")]
    [InlineData(null)]
    public void PublicIpv6_WithAPrivateCidrOrNone_IsAccepted(string? cidr)
    {
        var result = _validator.TestValidate(new CreateNetworkCommand("net-1", "singapore01", cidr, EnablePublicIpv6: true));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("8.8.8.0/24")]
    [InlineData("172.32.0.0/24")]
    [InlineData("100.64.0.0/24")]
    [InlineData("9.0.0.0/7")]
    [InlineData("10.0.0.0/40")]
    [InlineData("10.0.0.300/24")]
    public void PublicIpv6_WithACidrOutsideThePrivateRanges_IsRejected(string cidr)
    {
        var result = _validator.TestValidate(new CreateNetworkCommand("net-1", "singapore01", cidr, EnablePublicIpv6: true));

        result.ShouldHaveValidationErrorFor(c => c.Cidr);
    }

    // A network without public IPv6 keeps today's rule: any IPv4 CIDR.
    [Fact]
    public void WithoutPublicIpv6_AnyIpv4Cidr_IsAccepted()
    {
        var result = _validator.TestValidate(new CreateNetworkCommand("net-1", "mock", "8.8.8.0/24"));

        result.ShouldNotHaveAnyValidationErrors();
    }

    // The spec's pattern lets /33 and 999 through; the Domain's Ipv4Cidr does not, and the validator says so with the
    // same sentence as for a CIDR that is not one at all — one cause, one message.
    [Theory]
    [InlineData("10.0.0.0/40")]
    [InlineData("10.0.0.300/24")]
    [InlineData("10.0.0.0/33")]
    [InlineData("not-a-cidr")]
    public void AnIpv4Cidr_WithAPrefixOver32_OrAnOctetOver255_IsRejected_EvenWithoutPublicIpv6(string cidr)
    {
        var result = _validator.TestValidate(new CreateNetworkCommand("net-1", "mock", cidr));

        result.ShouldHaveValidationErrorFor(c => c.Cidr)
            .WithErrorMessage("'Cidr' must be a valid CIDR notation (e.g. 10.0.1.0/24).");
        Assert.Single(result.Errors);
    }

    // With public IPv6 a CIDR that does not parse gets the format message alone, not the private-range one as well.
    [Fact]
    public void PublicIpv6_WithACidrThatDoesNotParse_GetsOneMessage()
    {
        var result = _validator.TestValidate(new CreateNetworkCommand("net-1", "singapore01", "10.0.0.300/24", EnablePublicIpv6: true));

        var error = Assert.Single(result.Errors);
        Assert.Equal("'Cidr' must be a valid CIDR notation (e.g. 10.0.1.0/24).", error.ErrorMessage);
    }
}
